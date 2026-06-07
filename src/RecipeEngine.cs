using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Bo_Tron_Khi_CS
{
    public enum RecipeState
    {
        Idle,
        PreStabilization,
        Stabilization,
        Exposure,
        Recovery
    }

    public class RecipeProgressEventArgs : EventArgs
    {
        public int ActiveStepIndex { get; }
        public RecipeState State { get; }
        public int RemainingSeconds { get; }
        public string Message { get; }
        public RecipeProgressEventArgs(int stepIdx, RecipeState state, int remSec, string msg)
        {
            ActiveStepIndex = stepIdx;
            State = state;
            RemainingSeconds = remSec;
            Message = msg;
        }
    }

    public class RecipeEngine
    {
        private readonly ModbusHandler _handler;
        private readonly SystemConfig _config;
        private CancellationTokenSource _cts;
        private Task _runTask;

        public bool IsRunning { get; private set; } = false;
        public RecipeState CurrentState { get; private set; } = RecipeState.Idle;
        public int ActiveStepIndex { get; private set; } = -1;

        public event EventHandler<RecipeProgressEventArgs> ProgressUpdated;
        public event EventHandler RecipeCompleted;

        public RecipeEngine(ModbusHandler handler, SystemConfig config)
        {
            _handler = handler;
            _config = config;
        }

        public void Start(List<RecipeStep> steps)
        {
            if (IsRunning) return;
            if (steps == null || steps.Count == 0) return;

            IsRunning = true;
            _cts = new CancellationTokenSource();
            _runTask = Task.Run(() => RunSequence(steps, _cts.Token), _cts.Token);
        }

        public void Stop()
        {
            if (!IsRunning) return;
            _cts?.Cancel();
            try
            {
                _runTask?.Wait(2000);
            }
            catch { }
            IsRunning = false;
            CurrentState = RecipeState.Idle;
            ActiveStepIndex = -1;

            // Safe shutdown of flows
            SafeShutdownFlows();
        }

        private void WriteMfcFlow(byte ms, int ch, double flow)
        {
            int chIdx = ch - 1; // 1-indexed to 0-indexed
            double factor = 1.0;
            if (_config.mfc_factor != null && _config.mfc_factor.Count > chIdx)
            {
                factor = _config.mfc_factor[chIdx];
            }

            var floatRegs = ModbusHandler.FloatToRegs((float)(flow * factor));
            _handler.WriteMultipleRegisters(ms, (ushort)(60 + chIdx * 3), new ushort[] { floatRegs[0], floatRegs[1] });
            _handler.WriteSingleRegister(ms, (ushort)(60 + chIdx * 3 + 2), (ushort)(flow > 0.1 ? 1 : 0));
        }

        private async Task RunSequence(List<RecipeStep> steps, CancellationToken token)
        {
            byte ms = (byte)_config.mixing_slave;
            byte es = (byte)_config.e5cc_slave;

            try
            {
                // 1. Initial Pre-Stabilization (Air Purge & Pre-mix)
                if (_config.stable_time > 0)
                {
                    CurrentState = RecipeState.PreStabilization;
                    ActiveStepIndex = 0;

                    int stableTime = _config.stable_time;
                    int gasOnTime = _config.gas_on_time;
                    int purgeTime = Math.Max(0, stableTime - gasOnTime);
                    int premixTime = Math.Min(stableTime, gasOnTime);

                    // --- PURGE PART ---
                    if (purgeTime > 0)
                    {
                        // Valve OFF (MFC1 Carrier -> Chamber, others -> Exhaust)
                        _handler.WriteSingleRegister(ms, 20, 0); 
                        _handler.WriteSingleRegister(ms, 21, 1); // Pump ON
                        
                        WriteMfcFlow(ms, 1, _config.total_flow);
                        for (int ch = 2; ch <= 6; ch++)
                        {
                            WriteMfcFlow(ms, ch, 0.0);
                        }

                        double elapsed = 0;
                        while (elapsed < purgeTime)
                        {
                            token.ThrowIfCancellationRequested();
                            double rem = stableTime - elapsed;
                            ProgressUpdated?.Invoke(this, new RecipeProgressEventArgs(-1, CurrentState, (int)Math.Ceiling(rem), $"Pre-Stabilization (Air Purge) - Rem: {(int)Math.Ceiling(rem)}s"));
                            await Task.Delay(500, token);
                            elapsed += 0.5;
                        }
                    }

                    // --- PRE-MIX PART ---
                    if (premixTime > 0 && steps.Count > 0)
                    {
                        ApplyStepFlows(steps[0], ms, es);
                        _handler.WriteSingleRegister(ms, 20, 0); // Ensure valve remains OFF

                        double elapsed = 0;
                        while (elapsed < premixTime)
                        {
                            token.ThrowIfCancellationRequested();
                            double rem = premixTime - elapsed;
                            ProgressUpdated?.Invoke(this, new RecipeProgressEventArgs(-1, CurrentState, (int)Math.Ceiling(rem), $"Pre-Stabilization (Pre-mixing) - Rem: {(int)Math.Ceiling(rem)}s"));
                            await Task.Delay(500, token);
                            elapsed += 0.5;
                        }
                    }
                }

                // 2. Loop through each recipe step
                for (int stepIdx = 0; stepIdx < steps.Count; stepIdx++)
                {
                    token.ThrowIfCancellationRequested();
                    ActiveStepIndex = stepIdx;
                    RecipeStep step = steps[stepIdx];

                    // --- STABILIZATION (HEATING) PHASE ---
                    CurrentState = RecipeState.Stabilization;

                    // Write target temperature and RUN E5CC
                    ushort tempReg = (ushort)(step.Temp * 10);
                    _handler.WriteSingleRegister(es, 0x2100, tempReg);
                    _handler.WriteSingleRegister(es, 0x0000, 0); // RUN
                    
                    // Keep Valve OFF during stabilization (flush chamber with carrier)
                    _handler.WriteSingleRegister(ms, 20, 0); 
                    _handler.WriteSingleRegister(ms, 21, 1); // Pump ON
                    
                    // Purging: MFC1 (Carrier) = Total Flow, all others = 0
                    WriteMfcFlow(ms, 1, _config.total_flow);
                    for (int ch = 2; ch <= 6; ch++)
                    {
                        WriteMfcFlow(ms, ch, 0.0);
                    }

                    double currentTemp = 25.0;
                    DateTime stabilizationStart = DateTime.Now;
                    bool tempReached = false;

                    while (!tempReached)
                    {
                        token.ThrowIfCancellationRequested();

                        try
                        {
                            var regs = _handler.ReadHoldingRegisters(es, 0x2000, 1);
                            if (regs != null && regs.Length > 0)
                            {
                                currentTemp = regs[0] / 10.0;
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error reading temperature in stabilization: {ex.Message}");
                        }

                        if (Math.Abs(currentTemp - step.Temp) <= 1.5)
                        {
                            tempReached = true;
                        }
                        else if ((DateTime.Now - stabilizationStart).TotalSeconds > 600)
                        {
                            Console.WriteLine("Stabilization timeout reached. Proceeding to Exposure.");
                            tempReached = true;
                        }

                        if (!tempReached)
                        {
                            ProgressUpdated?.Invoke(this, new RecipeProgressEventArgs(stepIdx, CurrentState, 0, $"Step {stepIdx + 1}/{steps.Count} - Heating ({currentTemp:F1}°C → {step.Temp:F1}°C)"));
                            await Task.Delay(1000, token);
                        }
                    }

                    // --- EXPOSURE PHASE ---
                    CurrentState = RecipeState.Exposure;
                    
                    // Apply target setpoints and turn Valve ON (Relay 1 = 1)
                    ApplyStepFlows(step, ms, es);
                    _handler.WriteSingleRegister(ms, 20, 1); // Valve ON (MFC2-6 -> Chamber)
                    _handler.WriteSingleRegister(ms, 21, 1); // Pump ON

                    double expTime = step.ExposureTime;
                    double expElapsed = 0;
                    while (expElapsed < expTime)
                    {
                        token.ThrowIfCancellationRequested();
                        double rem = expTime - expElapsed;
                        ProgressUpdated?.Invoke(this, new RecipeProgressEventArgs(stepIdx, CurrentState, (int)Math.Ceiling(rem), $"Step {stepIdx + 1}/{steps.Count} - Exposure Phase - Rem: {(int)Math.Ceiling(rem)}s"));
                        await Task.Delay(500, token);
                        expElapsed += 0.5;
                    }

                    // --- RECOVERY PHASE ---
                    CurrentState = RecipeState.Recovery;
                    
                    int recTime = step.RecoveryTime;
                    int gasOnTime = _config.gas_on_time;
                    bool hasNext = (stepIdx + 1 < steps.Count);
                    
                    int recPurgeTime = hasNext ? Math.Max(0, recTime - gasOnTime) : recTime;
                    int recPremixTime = hasNext ? Math.Min(recTime, gasOnTime) : 0;

                    // --- RECOVERY PURGE ---
                    if (recPurgeTime > 0)
                    {
                        // Valve OFF (MFC1 Carrier -> Chamber, MFC2-6 -> Exhaust)
                        _handler.WriteSingleRegister(ms, 20, 0); 
                        _handler.WriteSingleRegister(ms, 21, 1); // Pump ON
                        
                        WriteMfcFlow(ms, 1, _config.total_flow);
                        for (int ch = 2; ch <= 6; ch++)
                        {
                            WriteMfcFlow(ms, ch, 0.0);
                        }

                        double elapsed = 0;
                        while (elapsed < recPurgeTime)
                        {
                            token.ThrowIfCancellationRequested();
                            double rem = recTime - elapsed;
                            ProgressUpdated?.Invoke(this, new RecipeProgressEventArgs(stepIdx, CurrentState, (int)Math.Ceiling(rem), $"Step {stepIdx + 1}/{steps.Count} - Recovery Purge - Rem: {(int)Math.Ceiling(rem)}s"));
                            await Task.Delay(500, token);
                            elapsed += 0.5;
                        }
                    }

                    // --- RECOVERY PRE-MIX ---
                    if (recPremixTime > 0 && hasNext)
                    {
                        ApplyStepFlows(steps[stepIdx + 1], ms, es);
                        _handler.WriteSingleRegister(ms, 20, 0); // Ensure valve remains OFF

                        double elapsed = 0;
                        while (elapsed < recPremixTime)
                        {
                            token.ThrowIfCancellationRequested();
                            double rem = recPremixTime - elapsed;
                            ProgressUpdated?.Invoke(this, new RecipeProgressEventArgs(stepIdx, CurrentState, (int)Math.Ceiling(rem), $"Step {stepIdx + 1}/{steps.Count} - Recovery Pre-mixing - Rem: {(int)Math.Ceiling(rem)}s"));
                            await Task.Delay(500, token);
                            elapsed += 0.5;
                        }
                    }
                }

                // Completed
                SafeShutdownFlows();
                IsRunning = false;
                RecipeCompleted?.Invoke(this, EventArgs.Empty);
            }
            catch (OperationCanceledException)
            {
                // Task was canceled
            }
            catch (Exception ex)
            {
                ProgressUpdated?.Invoke(this, new RecipeProgressEventArgs(-1, RecipeState.Idle, 0, $"Error: {ex.Message}"));
                SafeShutdownFlows();
                IsRunning = false;
            }
        }

        private void ApplyStepFlows(RecipeStep step, byte ms, byte es)
        {
            double tot = _config.total_flow;
            double co1 = _config.co1;
            double co2 = _config.co2;
            double co3 = _config.co3;

            // Flow rates math
            double q1 = (co1 > 0) ? (step.Gas1Ppm / co1) * tot : 0;
            double qmfc3 = (q1 <= 100) ? q1 : 0; // Gas 1 Low
            double qmfc4 = (q1 <= 100) ? 0 : q1; // Gas 1 High
            double qmfc5 = (co2 > 0) ? (step.Gas2Ppm / co2) * tot : 0; // Gas 2
            double qmfc6 = (co3 > 0) ? (step.Gas3Ppm / co3) * tot : 0; // Gas 3

            double qmfc2 = Math.Max(0.0, tot - qmfc3 - qmfc4 - qmfc5 - qmfc6); // Diluent Air/N2

            // Set temperature setpoint to E5CC
            ushort tempReg = (ushort)(step.Temp * 10);
            _handler.WriteSingleRegister(es, 0x2100, tempReg);
            _handler.WriteSingleRegister(es, 0x0000, 0); // Ensure E5CC is in RUN mode (write 0x0000)

            // Write MFC setpoints and enable DACs
            WriteMfcFlow(ms, 1, tot); // MFC1 Carrier
            WriteMfcFlow(ms, 2, qmfc2); // MFC2 Diluent
            WriteMfcFlow(ms, 3, qmfc3); // MFC3 Gas 1 Low
            WriteMfcFlow(ms, 4, qmfc4); // MFC4 Gas 1 High
            WriteMfcFlow(ms, 5, qmfc5); // MFC5 Gas 2
            WriteMfcFlow(ms, 6, qmfc6); // MFC6 Gas 3
        }

        private void SafeShutdownFlows()
        {
            try
            {
                byte ms = (byte)_config.mixing_slave;
                // Close Valve (Relay 1 = 0) and Keep Pump ON/OFF based on settings
                _handler.WriteSingleRegister(ms, 20, 0); 

                // Set all MFC flows to 0 and turn off all DACs
                for (int ch = 1; ch <= 6; ch++)
                {
                    WriteMfcFlow(ms, ch, 0.0);
                }
            }
            catch { }
        }
    }
}
