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

        private async Task RunSequence(List<RecipeStep> steps, CancellationToken token)
        {
            byte ms = (byte)_config.mixing_slave;
            byte es = (byte)_config.e5cc_slave;

            try
            {
                // 1. Initial Pre-Stabilization (Air Purge)
                if (_config.stable_time > 0)
                {
                    CurrentState = RecipeState.PreStabilization;
                    ActiveStepIndex = 0;
                    
                    double stableTime = _config.stable_time;
                    double elapsed = 0;
                    bool premixTriggered = false;

                    while (elapsed < stableTime)
                    {
                        token.ThrowIfCancellationRequested();

                        double rem = stableTime - elapsed;
                        
                        // Check for pre-mix condition: when within gas_on_time before exposure starts
                        if (_config.gas_on_time > 0 && rem <= _config.gas_on_time && steps.Count > 0)
                        {
                            if (!premixTriggered)
                            {
                                premixTriggered = true;
                                ApplyStepFlows(steps[0], ms, es);
                            }
                        }
                        else
                        {
                            // Purging: MFC1 (Carrier) = Total Flow, all others = 0, Relay 1 (Valve) = OFF, Relay 2 (Pump) = ON
                            _handler.WriteSingleRegister(ms, 20, 0); // Valve OFF (MFC1 -> Chamber)
                            _handler.WriteSingleRegister(ms, 21, 1); // Pump ON
                            
                            // Set MFC1 to total flow, others to 0
                            var carrierRegs = ModbusHandler.FloatToRegs((float)_config.total_flow);
                            _handler.WriteMultipleRegisters(ms, 60, new ushort[] { carrierRegs[0], carrierRegs[1], 1 }); // MFC1 SP & DAC ON
                            
                            for (int ch = 1; ch < 6; ch++)
                            {
                                _handler.WriteSingleRegister(ms, (ushort)(60 + ch * 3 + 2), 0); // Disable other DACs
                            }
                        }

                        ProgressUpdated?.Invoke(this, new RecipeProgressEventArgs(-1, CurrentState, (int)Math.Ceiling(rem), $"Pre-Stabilization (Air Purge) - Rem: {(int)Math.Ceiling(rem)}s"));
                        await Task.Delay(500, token);
                        elapsed += 0.5;
                    }
                }

                // 2. Loop through each recipe step
                for (int stepIdx = 0; stepIdx < steps.Count; stepIdx++)
                {
                    token.ThrowIfCancellationRequested();
                    ActiveStepIndex = stepIdx;
                    RecipeStep step = steps[stepIdx];

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
                    
                    // Valve OFF (MFC1 Carrier -> Chamber, MFC2-6 -> Exhaust)
                    _handler.WriteSingleRegister(ms, 20, 0); 
                    
                    // Set MFC1 to total flow, disable other MFCs to save gas
                    var carrierRegs = ModbusHandler.FloatToRegs((float)_config.total_flow);
                    _handler.WriteMultipleRegisters(ms, 60, new ushort[] { carrierRegs[0], carrierRegs[1], 1 });
                    for (int ch = 1; ch < 6; ch++)
                    {
                        _handler.WriteSingleRegister(ms, (ushort)(60 + ch * 3 + 2), 0); // DAC off
                    }

                    double recTime = step.RecoveryTime;
                    double recElapsed = 0;
                    while (recElapsed < recTime)
                    {
                        token.ThrowIfCancellationRequested();
                        double rem = recTime - recElapsed;
                        ProgressUpdated?.Invoke(this, new RecipeProgressEventArgs(stepIdx, CurrentState, (int)Math.Ceiling(rem), $"Step {stepIdx + 1}/{steps.Count} - Recovery Phase - Rem: {(int)Math.Ceiling(rem)}s"));
                        await Task.Delay(500, token);
                        recElapsed += 0.5;
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
            // MFC1 (Carrier) - run at total flow to vent exhaust line
            var q1Regs = ModbusHandler.FloatToRegs((float)tot);
            _handler.WriteMultipleRegisters(ms, 60, new ushort[] { q1Regs[0], q1Regs[1] });
            _handler.WriteSingleRegister(ms, 62, 1);

            // MFC2 (Diluent)
            var q2Regs = ModbusHandler.FloatToRegs((float)qmfc2);
            _handler.WriteMultipleRegisters(ms, 63, new ushort[] { q2Regs[0], q2Regs[1] });
            _handler.WriteSingleRegister(ms, 65, qmfc2 > 0.1 ? (ushort)1 : (ushort)0);

            // MFC3 (G1 Low)
            var q3Regs = ModbusHandler.FloatToRegs((float)qmfc3);
            _handler.WriteMultipleRegisters(ms, 66, new ushort[] { q3Regs[0], q3Regs[1] });
            _handler.WriteSingleRegister(ms, 68, qmfc3 > 0.1 ? (ushort)1 : (ushort)0);

            // MFC4 (G1 High)
            var q4Regs = ModbusHandler.FloatToRegs((float)qmfc4);
            _handler.WriteMultipleRegisters(ms, 69, new ushort[] { q4Regs[0], q4Regs[1] });
            _handler.WriteSingleRegister(ms, 71, qmfc4 > 0.1 ? (ushort)1 : (ushort)0);

            // MFC5 (Gas 2)
            var q5Regs = ModbusHandler.FloatToRegs((float)qmfc5);
            _handler.WriteMultipleRegisters(ms, 72, new ushort[] { q5Regs[0], q5Regs[1] });
            _handler.WriteSingleRegister(ms, 74, qmfc5 > 0.1 ? (ushort)1 : (ushort)0);

            // MFC6 (Gas 3)
            var q6Regs = ModbusHandler.FloatToRegs((float)qmfc6);
            _handler.WriteMultipleRegisters(ms, 75, new ushort[] { q6Regs[0], q6Regs[1] });
            _handler.WriteSingleRegister(ms, 77, qmfc6 > 0.1 ? (ushort)1 : (ushort)0);
        }

        private void SafeShutdownFlows()
        {
            try
            {
                byte ms = (byte)_config.mixing_slave;
                // Close Valve (Relay 1 = 0) and Keep Pump ON/OFF based on settings
                _handler.WriteSingleRegister(ms, 20, 0); 

                // Set all MFC flows to 0 and turn off all DACs
                var zeroRegs = ModbusHandler.FloatToRegs(0.0f);
                for (int ch = 0; ch < 6; ch++)
                {
                    _handler.WriteMultipleRegisters(ms, (ushort)(60 + ch * 3), new ushort[] { zeroRegs[0], zeroRegs[1] });
                    _handler.WriteSingleRegister(ms, (ushort)(60 + ch * 3 + 2), 0); // DAC off
                }
            }
            catch { }
        }
    }
}
