using System;
using System.Diagnostics;
using System.Threading;

namespace Bo_Tron_Khi_CS
{
    public class PolledDataEventArgs : EventArgs
    {
        public PolledData Data { get; }
        public PolledDataEventArgs(PolledData data) => Data = data;
    }

    public class PolledData
    {
        // Mixing Board (Slave 2)
        public float[] SccmPV { get; set; } = new float[6];
        public float[] SccmSP { get; set; } = new float[6];
        public ushort[] DacEnable { get; set; } = new ushort[6];
        public ushort Relay1 { get; set; }
        public ushort Relay2 { get; set; }
        public ushort BoardStatus { get; set; }

        // E5CC (Slave 1)
        public float E5ccPV { get; set; }
        public float E5ccSP { get; set; }
        public float E5ccMV { get; set; }
        public ushort E5ccStatus { get; set; }
        
        // Error tracking
        public string ErrorMessage { get; set; }
        public bool IsError => !string.IsNullOrEmpty(ErrorMessage);

        // Communication quality indicators
        public int TotalRetries { get; set; }    // sum of retries across all transactions this cycle
        public int FailedTransactions { get; set; } // how many individual reads failed this cycle
    }

    public class PollingEngine
    {
        private readonly ModbusHandler _handler;
        private readonly SystemConfig _config;
        private Thread _thread;
        private CancellationTokenSource _cts;
        private int _targetIntervalMs = 500;

        // Adaptive polling state
        private int _currentIntervalMs = 500;
        private int _consecutiveCycleErrors = 0;
        private const int ErrorSlowdownThreshold = 3;  // after 3 bad cycles, slow down
        private const int SlowPollIntervalMs = 2000;    // degraded polling rate
        private const int MinPollIntervalMs = 100;

        public event EventHandler<PolledDataEventArgs> DataPolled;
        public PolledData LastData { get; private set; }

        /// <summary>
        /// True if polling has degraded due to repeated errors.
        /// </summary>
        public bool IsDegraded => _consecutiveCycleErrors >= ErrorSlowdownThreshold;

        public PollingEngine(ModbusHandler handler, SystemConfig config)
        {
            _handler = handler;
            _config = config;
            _targetIntervalMs = (int)(config.poll_interval * 1000);
            if (_targetIntervalMs < MinPollIntervalMs) _targetIntervalMs = MinPollIntervalMs;
            _currentIntervalMs = _targetIntervalMs;
        }

        public void Start()
        {
            if (_cts != null) return; // already running

            _cts = new CancellationTokenSource();
            _thread = new Thread(() => PollLoop(_cts.Token)) { IsBackground = true };
            _thread.Start();
        }

        public void Stop()
        {
            var cts = _cts;
            if (cts == null) return;

            cts.Cancel(); // immediately signals the CancellationToken
            _thread?.Join(2000); // wait up to 2s for clean exit
            _cts = null;
            _thread = null;
        }

        private void PollLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                var cycleTimer = Stopwatch.StartNew();
                var data = new PolledData();
                int totalRetries = 0;
                int failedTx = 0;

                try
                {
                    if (_handler.IsConnected)
                    {
                        // 1. Poll Mixing Board Input Registers (Slave 2) — Flow PVs
                        var inputsResult = _handler.TryReadInputRegisters((byte)_config.mixing_slave, 0, 13, ct);
                        if (inputsResult.Success && inputsResult.Data != null && inputsResult.Data.Length >= 13)
                        {
                            for (int i = 0; i < 6; i++)
                            {
                                data.SccmPV[i] = ModbusHandler.RegsToFloat(inputsResult.Data[i * 2], inputsResult.Data[i * 2 + 1]);
                            }
                            data.BoardStatus = inputsResult.Data[12];
                            totalRetries += inputsResult.RetryCount;
                        }
                        else
                        {
                            failedTx++;
                            totalRetries += inputsResult.RetryCount;
                        }

                        // 2. Poll Mixing Board Holding Registers (Slave 2) — Setpoints + DAC enable
                        var holdResult = _handler.TryReadHoldingRegisters((byte)_config.mixing_slave, 60, 18, ct);
                        if (holdResult.Success && holdResult.Data != null && holdResult.Data.Length >= 18)
                        {
                            for (int i = 0; i < 6; i++)
                            {
                                data.SccmSP[i] = ModbusHandler.RegsToFloat(holdResult.Data[i * 3], holdResult.Data[i * 3 + 1]);
                                data.DacEnable[i] = holdResult.Data[i * 3 + 2];
                            }
                            totalRetries += holdResult.RetryCount;
                        }
                        else
                        {
                            failedTx++;
                            totalRetries += holdResult.RetryCount;
                        }

                        // 3. Poll Relay states
                        var relayResult = _handler.TryReadHoldingRegisters((byte)_config.mixing_slave, 20, 2, ct);
                        if (relayResult.Success && relayResult.Data != null && relayResult.Data.Length >= 2)
                        {
                            data.Relay1 = relayResult.Data[0];
                            data.Relay2 = relayResult.Data[1];
                            totalRetries += relayResult.RetryCount;
                        }
                        else
                        {
                            failedTx++;
                            totalRetries += relayResult.RetryCount;
                        }

                        // 4. Poll E5CC Holding Registers (Slave 1) — Temp PV, SP, MV
                        var e5Result = _handler.TryReadHoldingRegisters((byte)_config.e5cc_slave, 0x2000, 3, ct);
                        if (e5Result.Success && e5Result.Data != null && e5Result.Data.Length >= 3)
                        {
                            data.E5ccPV = (short)e5Result.Data[0] / 10.0f;
                            data.E5ccSP = (short)e5Result.Data[1] / 10.0f;
                            data.E5ccMV = (short)e5Result.Data[2] / 10.0f;
                            totalRetries += e5Result.RetryCount;
                        }
                        else
                        {
                            failedTx++;
                            totalRetries += e5Result.RetryCount;
                        }

                        // 5. Poll E5CC Status
                        var e5StatusResult = _handler.TryReadHoldingRegisters((byte)_config.e5cc_slave, 0x0100, 1, ct);
                        if (e5StatusResult.Success && e5StatusResult.Data != null && e5StatusResult.Data.Length >= 1)
                        {
                            data.E5ccStatus = e5StatusResult.Data[0];
                            totalRetries += e5StatusResult.RetryCount;
                        }
                        else
                        {
                            failedTx++;
                            totalRetries += e5StatusResult.RetryCount;
                        }

                        // Record quality metrics
                        data.TotalRetries = totalRetries;
                        data.FailedTransactions = failedTx;

                        // Partial success is still valid data
                        if (failedTx > 0 && failedTx < 5)
                        {
                            // Some transactions failed but we got partial data — still usable
                            data.ErrorMessage = null; // not a full error
                        }
                        else if (failedTx >= 5)
                        {
                            data.ErrorMessage = "All Modbus transactions failed";
                        }
                    }
                    else
                    {
                        data.ErrorMessage = "Modbus connection closed.";
                        failedTx = 5;
                    }
                }
                catch (OperationCanceledException)
                {
                    break; // clean exit on cancellation
                }
                catch (Exception ex)
                {
                    data.ErrorMessage = ex.Message;
                    failedTx = 5;
                }

                // Adaptive interval adjustment
                if (failedTx >= 3)
                {
                    _consecutiveCycleErrors++;
                    if (_consecutiveCycleErrors >= ErrorSlowdownThreshold)
                    {
                        _currentIntervalMs = SlowPollIntervalMs; // back-pressure: slow down
                    }
                }
                else
                {
                    if (_consecutiveCycleErrors > 0)
                    {
                        _consecutiveCycleErrors = 0;
                        _currentIntervalMs = _targetIntervalMs; // restore normal speed
                    }
                }

                // Raise event
                LastData = data;
                DataPolled?.Invoke(this, new PolledDataEventArgs(data));

                // Adaptive sleep: target interval minus actual transaction time
                cycleTimer.Stop();
                int elapsedMs = (int)cycleTimer.ElapsedMilliseconds;
                int sleepMs = Math.Max(10, _currentIntervalMs - elapsedMs);

                // Use WaitHandle.WaitOne for cancellable sleep (instead of Thread.Sleep)
                try
                {
                    ct.WaitHandle.WaitOne(sleepMs);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
            }
        }
    }
}
