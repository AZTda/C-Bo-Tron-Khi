using System;
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
    }

    public class PollingEngine
    {
        private readonly ModbusHandler _handler;
        private readonly SystemConfig _config;
        private Thread _thread;
        private bool _isRunning = false;
        private int _intervalMs = 500;

        public event EventHandler<PolledDataEventArgs> DataPolled;
        public PolledData LastData { get; private set; }

        public PollingEngine(ModbusHandler handler, SystemConfig config)
        {
            _handler = handler;
            _config = config;
            _intervalMs = (int)(config.poll_interval * 1000);
            if (_intervalMs < 100) _intervalMs = 100;
        }

        public void Start()
        {
            if (_isRunning) return;
            _isRunning = true;
            _thread = new Thread(PollLoop) { IsBackground = true };
            _thread.Start();
        }

        public void Stop()
        {
            _isRunning = false;
            _thread?.Join(1000);
            _thread = null;
        }

        private void PollLoop()
        {
            while (_isRunning)
            {
                var data = new PolledData();
                try
                {
                    if (_handler.IsConnected)
                    {
                        // 1. Poll Mixing Board Inputs (Slave 2)
                        var inputs = _handler.ReadInputRegisters((byte)_config.mixing_slave, 0, 13);
                        if (inputs != null && inputs.Length >= 13)
                        {
                            for (int i = 0; i < 6; i++)
                            {
                                data.SccmPV[i] = ModbusHandler.RegsToFloat(inputs[i * 2], inputs[i * 2 + 1]);
                            }
                            data.BoardStatus = inputs[12];
                        }

                        // 2. Poll Mixing Board Control Holdregs (Slave 2)
                        var holdCtrl = _handler.ReadHoldingRegisters((byte)_config.mixing_slave, 60, 18);
                        if (holdCtrl != null && holdCtrl.Length >= 18)
                        {
                            for (int i = 0; i < 6; i++)
                            {
                                data.SccmSP[i] = ModbusHandler.RegsToFloat(holdCtrl[i * 3], holdCtrl[i * 3 + 1]);
                                data.DacEnable[i] = holdCtrl[i * 3 + 2];
                            }
                        }

                        var relays = _handler.ReadHoldingRegisters((byte)_config.mixing_slave, 20, 2);
                        if (relays != null && relays.Length >= 2)
                        {
                            data.Relay1 = relays[0];
                            data.Relay2 = relays[1];
                        }

                        // 3. Poll E5CC Holdregs (Slave 1)
                        var e5Data = _handler.ReadHoldingRegisters((byte)_config.e5cc_slave, 0x2000, 3);
                        if (e5Data != null && e5Data.Length >= 3)
                        {
                            data.E5ccPV = (short)e5Data[0] / 10.0f;
                            data.E5ccSP = (short)e5Data[1] / 10.0f;
                            data.E5ccMV = (short)e5Data[2] / 10.0f;
                        }

                        var e5Status = _handler.ReadHoldingRegisters((byte)_config.e5cc_slave, 0x0100, 1);
                        if (e5Status != null && e5Status.Length >= 1)
                        {
                            data.E5ccStatus = e5Status[0];
                        }
                    }
                    else
                    {
                        data.ErrorMessage = "Modbus connection closed.";
                    }
                }
                catch (Exception ex)
                {
                    data.ErrorMessage = ex.Message;
                }

                // Raise event
                LastData = data;
                DataPolled?.Invoke(this, new PolledDataEventArgs(data));

                Thread.Sleep(_intervalMs);
            }
        }
    }
}
