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

        // ===================================================
        // ATOMIC MFC WRITE — single WriteMultiple for SP + DAC_EN
        // Replaces old pattern of 2 separate calls per channel
        // ===================================================
        private void WriteMfcFlow(byte ms, int ch, double flow, CancellationToken ct)
        {
            int chIdx = ch - 1; // 1-indexed to 0-indexed
            double factor = 1.0;
            if (_config.mfc_factor != null && _config.mfc_factor.Count > chIdx)
            {
                factor = _config.mfc_factor[chIdx];
            }

            // Batch: [SP_Hi, SP_Lo, DAC_EN] = 3 registers in ONE transaction
            var floatRegs = ModbusHandler.FloatToRegs((float)(flow * factor));
            ushort dacEn = (ushort)(flow > 0.1 ? 1 : 0);
            ushort[] batch = new ushort[] { floatRegs[0], floatRegs[1], dacEn };

            var result = _handler.TryWriteMultipleRegisters(ms, (ushort)(60 + chIdx * 3), batch, ct);
            if (!result.Success)
            {
                Console.WriteLine($"WriteMfcFlow CH{ch} failed: {result.ErrorMessage}");
            }
        }

        // Legacy overload without CancellationToken (for SafeShutdownFlows)
        private void WriteMfcFlow(byte ms, int ch, double flow)
        {
            WriteMfcFlow(ms, ch, flow, CancellationToken.None);
        }

        // ===================================================
        // BATCH WRITE ALL 6 MFCs — 18 registers in ONE transaction
        // ===================================================
        private void BatchWriteAllMfcFlows(byte ms, double[] flows, CancellationToken ct)
        {
            // Build 18-register batch: [SP_Hi, SP_Lo, DAC_EN] × 6 channels
            ushort[] batch = new ushort[18];
            for (int i = 0; i < 6; i++)
            {
                double flow = flows[i];
                double factor = 1.0;
                if (_config.mfc_factor != null && _config.mfc_factor.Count > i)
                {
                    factor = _config.mfc_factor[i];
                }

                var floatRegs = ModbusHandler.FloatToRegs((float)(flow * factor));
                batch[i * 3] = floatRegs[0];
                batch[i * 3 + 1] = floatRegs[1];
                batch[i * 3 + 2] = (ushort)(flow > 0.1 ? 1 : 0);
            }

            var result = _handler.TryWriteMultipleRegisters(ms, 60, batch, ct);
            if (!result.Success)
            {
                Console.WriteLine($"BatchWriteAllMfcFlows failed: {result.ErrorMessage}");
                // Fallback: try individual writes
                for (int ch = 1; ch <= 6; ch++)
                {
                    WriteMfcFlow(ms, ch, flows[ch - 1], ct);
                }
            }
        }

        // ===================================================
        // WRITE WITH VERIFY — write then read-back to confirm
        // Used for critical relay operations
        // ===================================================
        private bool WriteAndVerifyRelay(byte ms, ushort regAddr, ushort value, CancellationToken ct, int maxAttempts = 3)
        {
            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                var writeResult = _handler.TryWriteSingleRegister(ms, regAddr, value, ct);
                if (!writeResult.Success) continue;

                // Read back to verify
                var readResult = _handler.TryReadHoldingRegisters(ms, regAddr, 1, ct);
                if (readResult.Success && readResult.Data != null && readResult.Data.Length >= 1)
                {
                    if (readResult.Data[0] == value)
                        return true; // Verified!
                }

                // Mismatch or read failed — retry
                Console.WriteLine($"WriteAndVerify reg {regAddr}: attempt {attempt + 1} verify failed, retrying...");
                Thread.Sleep(50);
            }

            Console.WriteLine($"WriteAndVerify reg {regAddr}: FAILED after {maxAttempts} attempts");
            return false;
        }

        // ===================================================
        // RECIPE SEQUENCE — passes CancellationToken throughout
        // ===================================================
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
                        // Valve OFF + Pump ON (with verify for relays)
                        WriteAndVerifyRelay(ms, 20, 0, token);
                        WriteAndVerifyRelay(ms, 21, 1, token);
                        
                        // All MFCs in one batch: Carrier = total, rest = 0
                        double[] purgeFlows = new double[] { _config.total_flow, 0, 0, 0, 0, 0 };
                        BatchWriteAllMfcFlows(ms, purgeFlows, token);

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
                        ApplyStepFlows(steps[0], ms, es, token);
                        _handler.TryWriteSingleRegister(ms, 20, 0, token); // Ensure valve remains OFF

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
                    _handler.TryWriteSingleRegister(es, 0x2100, tempReg, token);
                    _handler.TryWriteSingleRegister(es, 0x0000, 0, token); // RUN
                    
                    // Keep Valve OFF during stabilization + Pump ON (verified)
                    WriteAndVerifyRelay(ms, 20, 0, token);
                    WriteAndVerifyRelay(ms, 21, 1, token);
                    
                    // Purging: all MFCs in one batch
                    double[] stabFlows = new double[] { _config.total_flow, 0, 0, 0, 0, 0 };
                    BatchWriteAllMfcFlows(ms, stabFlows, token);

                    double currentTemp = 25.0;
                    DateTime stabilizationStart = DateTime.Now;
                    bool tempReached = false;

                    while (!tempReached)
                    {
                        token.ThrowIfCancellationRequested();

                        var regsResult = _handler.TryReadHoldingRegisters(es, 0x2000, 1, token);
                        if (regsResult.Success && regsResult.Data != null && regsResult.Data.Length > 0)
                        {
                            currentTemp = regsResult.Data[0] / 10.0;
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
                    
                    // Apply target setpoints (batch) and Valve ON (verified)
                    ApplyStepFlows(step, ms, es, token);
                    WriteAndVerifyRelay(ms, 20, 1, token); // Valve ON
                    WriteAndVerifyRelay(ms, 21, 1, token); // Pump ON

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
                        WriteAndVerifyRelay(ms, 20, 0, token); // Valve OFF
                        WriteAndVerifyRelay(ms, 21, 1, token); // Pump ON
                        
                        double[] recPurgeFlows = new double[] { _config.total_flow, 0, 0, 0, 0, 0 };
                        BatchWriteAllMfcFlows(ms, recPurgeFlows, token);

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
                        ApplyStepFlows(steps[stepIdx + 1], ms, es, token);
                        _handler.TryWriteSingleRegister(ms, 20, 0, token); // Ensure valve remains OFF

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

        // ===================================================
        // APPLY STEP FLOWS — uses batch write for all 6 MFCs
        // ===================================================
        private void ApplyStepFlows(RecipeStep step, byte ms, byte es, CancellationToken ct)
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
            _handler.TryWriteSingleRegister(es, 0x2100, tempReg, ct);
            _handler.TryWriteSingleRegister(es, 0x0000, 0, ct); // Ensure E5CC is in RUN mode

            // Write all 6 MFC setpoints in ONE batch (18 registers)
            double[] flows = new double[] { tot, qmfc2, qmfc3, qmfc4, qmfc5, qmfc6 };
            BatchWriteAllMfcFlows(ms, flows, ct);
        }

        private void SafeShutdownFlows()
        {
            try
            {
                byte ms = (byte)_config.mixing_slave;
                // Close Valve (Relay 1 = 0)
                _handler.TryWriteSingleRegister(ms, 20, 0);

                // Set all MFC flows to 0 in one batch
                double[] zeroFlows = new double[] { 0, 0, 0, 0, 0, 0 };
                BatchWriteAllMfcFlows(ms, zeroFlows, CancellationToken.None);
            }
            catch { }
        }
    }
}
