using System;
using System.IO;
using System.IO.Ports;
using System.Net.Sockets;
using System.Threading;

namespace Bo_Tron_Khi_CS
{
    public class ModbusHandler
    {
        private SerialPort _serialPort;
        private TcpClient _tcpClient;
        private NetworkStream _tcpStream;
        private readonly object _lock = new object();
        private ushort _transactionId = 0;

        // Config properties
        public string Port { get; set; } = "Virtual Sim";
        public int Baudrate { get; set; } = 19200;
        public string Parity { get; set; } = "E";
        public double Timeout { get; set; } = 0.5; // seconds
        public bool IsConnected { get; private set; } = false;
        public bool IsTcp { get; set; } = false;
        public string TcpIp { get; set; } = "127.0.0.1";
        public int TcpPort { get; set; } = 502;

        // Sim variables (for Virtual Sim)
        private readonly float[] _simSccmPV = new float[6];
        private readonly float[] _simSccmSP = new float[6] { 0, 0, 0, 0, 0, 0 };
        private readonly ushort[] _simDacEn = new ushort[6] { 0, 0, 0, 0, 0, 0 };
        private readonly float[] _simMinSccm = new float[6] { 0, 0, 0, 0, 0, 0 };
        private readonly float[] _simMaxSccm = new float[6] { 500, 500, 50, 200, 100, 100 };
        private readonly float[] _simMinV = new float[6] { 0, 0, 0, 0, 0, 0 };
        private readonly float[] _simMaxV = new float[6] { 5000, 5000, 5000, 5000, 5000, 5000 };
        private ushort _simRelay1 = 0;
        private ushort _simRelay2 = 0;

        private float _simE5ccPV = 25.0f;
        private float _simE5ccSP = 25.0f;
        private ushort _simE5ccRunStop = 1; // 1 = Stop, 0 = Run
        private ushort _simE5ccAT = 0;
        private ushort _simE5ccStatus = 1; // bit 0 = 1 (Stop)
        private ushort _simAlm1 = 500; // 50.0C
        private ushort _simAlm2 = 500;
        private ushort _simP = 100; // 10.0
        private ushort _simI = 240;
        private ushort _simD = 40;
        private ushort _simCtrlPeriod = 12;
        private ushort _simMvHi = 1000;
        private ushort _simMvLo = 0;
        private ushort _simInputShift = 0;
        private ushort _simSpHi = 3000;
        private ushort _simSpLo = 0;

        private readonly Random _rand = new Random();

        public bool Connect()
        {
            lock (_lock)
            {
                Disconnect();
                if (Port == "Virtual Sim")
                {
                    IsConnected = true;
                    return true;
                }

                try
                {
                    if (IsTcp)
                    {
                        _tcpClient = new TcpClient();
                        var result = _tcpClient.BeginConnect(TcpIp, TcpPort, null, null);
                        bool success = result.AsyncWaitHandle.WaitOne((int)(Timeout * 1000));
                        if (!success)
                        {
                            _tcpClient.Close();
                            throw new TimeoutException("TCP Connection timeout");
                        }
                        _tcpClient.EndConnect(result);
                        _tcpStream = _tcpClient.GetStream();
                        _tcpStream.ReadTimeout = (int)(Timeout * 1000);
                        _tcpStream.WriteTimeout = (int)(Timeout * 1000);
                    }
                    else
                    {
                        Parity p = System.IO.Ports.Parity.Even;
                        if (Parity == "O") p = System.IO.Ports.Parity.Odd;
                        else if (Parity == "N") p = System.IO.Ports.Parity.None;

                        _serialPort = new SerialPort(Port, Baudrate, p, 8, StopBits.One)
                        {
                            ReadTimeout = (int)(Timeout * 1000),
                            WriteTimeout = (int)(Timeout * 1000)
                        };
                        _serialPort.Open();
                    }
                    IsConnected = true;
                    return true;
                }
                catch (Exception)
                {
                    IsConnected = false;
                    return false;
                }
            }
        }

        public void Disconnect()
        {
            lock (_lock)
            {
                try
                {
                    _serialPort?.Close();
                    _serialPort = null;

                    _tcpStream?.Close();
                    _tcpStream = null;

                    _tcpClient?.Close();
                    _tcpClient = null;
                }
                catch { }
                IsConnected = false;
            }
        }

        public ushort[] ReadHoldingRegisters(byte slave, ushort startAddress, ushort count)
        {
            if (Port == "Virtual Sim")
            {
                UpdateSimulation();
                return SimReadHolding(slave, startAddress, count);
            }

            return PerformModbusTransaction(slave, 0x03, startAddress, count, null);
        }

        public ushort[] ReadInputRegisters(byte slave, ushort startAddress, ushort count)
        {
            if (Port == "Virtual Sim")
            {
                UpdateSimulation();
                return SimReadInput(slave, startAddress, count);
            }

            return PerformModbusTransaction(slave, 0x04, startAddress, count, null);
        }

        public void WriteSingleRegister(byte slave, ushort address, ushort value)
        {
            if (Port == "Virtual Sim")
            {
                SimWriteSingle(slave, address, value);
                return;
            }

            PerformModbusTransaction(slave, 0x06, address, value, null);
        }

        public void WriteMultipleRegisters(byte slave, ushort startAddress, ushort[] values)
        {
            if (Port == "Virtual Sim")
            {
                SimWriteMultiple(slave, startAddress, values);
                return;
            }

            PerformModbusTransaction(slave, 0x10, startAddress, (ushort)values.Length, values);
        }

        private ushort[] PerformModbusTransaction(byte slave, byte functionCode, ushort addressOrQty, ushort countOrValue, ushort[] writeValues)
        {
            lock (_lock)
            {
                if (!IsConnected) throw new InvalidOperationException("Not connected to Modbus");

                byte[] request;
                if (IsTcp)
                {
                    _transactionId++;
                    int dataLen = (functionCode == 0x10) ? (7 + writeValues.Length * 2) : 6;
                    request = new byte[7 + dataLen];
                    
                    // MBAP Header
                    request[0] = (byte)(_transactionId >> 8);
                    request[1] = (byte)(_transactionId & 0xFF);
                    request[2] = 0; // Proto ID
                    request[3] = 0;
                    request[4] = (byte)(dataLen >> 8);
                    request[5] = (byte)(dataLen & 0xFF);
                    request[6] = slave;

                    // PDU
                    request[7] = functionCode;
                    request[8] = (byte)(addressOrQty >> 8);
                    request[9] = (byte)(addressOrQty & 0xFF);
                    request[10] = (byte)(countOrValue >> 8);
                    request[11] = (byte)(countOrValue & 0xFF);

                    if (functionCode == 0x10)
                    {
                        request[12] = (byte)(writeValues.Length * 2);
                        for (int i = 0; i < writeValues.Length; i++)
                        {
                            request[13 + i * 2] = (byte)(writeValues[i] >> 8);
                            request[14 + i * 2] = (byte)(writeValues[i] & 0xFF);
                        }
                    }
                }
                else
                {
                    // RTU Request
                    int reqLen = (functionCode == 0x10) ? (9 + writeValues.Length * 2) : 8;
                    request = new byte[reqLen];
                    request[0] = slave;
                    request[1] = functionCode;
                    request[2] = (byte)(addressOrQty >> 8);
                    request[3] = (byte)(addressOrQty & 0xFF);
                    request[4] = (byte)(countOrValue >> 8);
                    request[5] = (byte)(countOrValue & 0xFF);

                    if (functionCode == 0x10)
                    {
                        request[6] = (byte)(writeValues.Length * 2);
                        for (int i = 0; i < writeValues.Length; i++)
                        {
                            request[7 + i * 2] = (byte)(writeValues[i] >> 8);
                            request[8 + i * 2] = (byte)(writeValues[i] & 0xFF);
                        }
                    }

                    ushort crc = CalculateCRC(request, reqLen - 2);
                    request[reqLen - 2] = (byte)(crc & 0xFF);
                    request[reqLen - 1] = (byte)(crc >> 8);
                }

                // Send request
                if (IsTcp)
                {
                    _tcpStream.Write(request, 0, request.Length);
                }
                else
                {
                    _serialPort.DiscardInBuffer();
                    _serialPort.Write(request, 0, request.Length);
                }

                // Read response
                byte[] header = new byte[IsTcp ? 9 : 3];
                int readBytes = ReadBuffer(header, header.Length);
                if (readBytes < header.Length) throw new IOException("Timeout reading Modbus response header");

                byte responseFC = IsTcp ? header[7] : header[1];
                if ((responseFC & 0x80) != 0)
                {
                    // Exception response
                    int errCode = IsTcp ? header[8] : header[2];
                    throw new Exception($"Modbus Exception received: 0x{errCode:X2}");
                }

                if (functionCode == 0x03 || functionCode == 0x04)
                {
                    // Read functions: read the payload data
                    byte byteCount = IsTcp ? header[8] : header[2];
                    byte[] data = new byte[byteCount + (IsTcp ? 0 : 2)];
                    readBytes = ReadBuffer(data, data.Length);
                    if (readBytes < data.Length) throw new IOException("Timeout reading Modbus response data");

                    if (!IsTcp)
                    {
                        // Validate CRC
                        byte[] allResponse = new byte[header.Length + data.Length];
                        Buffer.BlockCopy(header, 0, allResponse, 0, header.Length);
                        Buffer.BlockCopy(data, 0, allResponse, header.Length, data.Length);
                        ushort responseCRC = (ushort)(data[byteCount] | (data[byteCount + 1] << 8));
                        if (CalculateCRC(allResponse, allResponse.Length - 2) != responseCRC)
                            throw new IOException("CRC mismatch in Modbus RTU response");
                    }

                    ushort[] registers = new ushort[byteCount / 2];
                    for (int i = 0; i < registers.Length; i++)
                    {
                        registers[i] = (ushort)((data[i * 2] << 8) | data[i * 2 + 1]);
                    }
                    return registers;
                }
                else
                {
                    // Write functions: read rest of frame (echo parameters or count)
                    byte[] remaining = new byte[IsTcp ? 3 : (functionCode == 0x10 ? 5 : 5)];
                    readBytes = ReadBuffer(remaining, remaining.Length);
                    if (readBytes < remaining.Length) throw new IOException("Timeout reading Modbus write response");

                    if (!IsTcp)
                    {
                        // Validate CRC
                        byte[] allResponse = new byte[header.Length + remaining.Length];
                        Buffer.BlockCopy(header, 0, allResponse, 0, header.Length);
                        Buffer.BlockCopy(remaining, 0, allResponse, header.Length, remaining.Length);
                        ushort responseCRC = (ushort)(remaining[remaining.Length - 2] | (remaining[remaining.Length - 1] << 8));
                        if (CalculateCRC(allResponse, allResponse.Length - 2) != responseCRC)
                            throw new IOException("CRC mismatch in Modbus RTU response");
                    }

                    return null;
                }
            }
        }

        private int ReadBuffer(byte[] buffer, int length)
        {
            int total = 0;
            while (total < length)
            {
                int read;
                if (IsTcp)
                {
                    read = _tcpStream.Read(buffer, total, length - total);
                }
                else
                {
                    read = _serialPort.Read(buffer, total, length - total);
                }
                if (read <= 0) break;
                total += read;
            }
            return total;
        }

        public static ushort CalculateCRC(byte[] data, int length)
        {
            ushort crc = 0xFFFF;
            for (int i = 0; i < length; i++)
            {
                crc ^= data[i];
                for (int j = 0; j < 8; j++)
                {
                    if ((crc & 0x0001) != 0)
                    {
                        crc >>= 1;
                        crc ^= 0xA001;
                    }
                    else
                    {
                        crc >>= 1;
                    }
                }
            }
            return crc;
        }

        public static float RegsToFloat(ushort hi, ushort lo)
        {
            byte[] bytes = new byte[4];
            bytes[0] = (byte)(lo & 0xFF);
            bytes[1] = (byte)(lo >> 8);
            bytes[2] = (byte)(hi & 0xFF);
            bytes[3] = (byte)(hi >> 8);
            return BitConverter.ToSingle(bytes, 0);
        }

        public static ushort[] FloatToRegs(float value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            ushort lo = (ushort)(bytes[0] | (bytes[1] << 8));
            ushort hi = (ushort)(bytes[2] | (bytes[3] << 8));
            return new ushort[] { hi, lo };
        }

        // ===================================================
        // DEVICE SIMULATION INTERNAL LOGIC
        // ===================================================
        private void UpdateSimulation()
        {
            // Simulate actual MFC flows heading towards setpoints if DAC is enabled
            for (int i = 0; i < 6; i++)
            {
                if (_simDacEn[i] == 1)
                {
                    float target = _simSccmSP[i];
                    _simSccmPV[i] += (target - _simSccmPV[i]) * 0.15f; // lag filter
                }
                else
                {
                    _simSccmPV[i] += (0.0f - _simSccmPV[i]) * 0.25f; // goes to 0
                }
                // Add minor noise
                if (_simSccmPV[i] > 0.5f)
                {
                    _simSccmPV[i] += (float)(_rand.NextDouble() - 0.5) * 0.1f;
                }
            }

            // Simulate E5CC temperature controller rising/falling to setpoint when RUNning
            if (_simE5ccRunStop == 0) // RUN
            {
                float target = _simE5ccSP;
                _simE5ccPV += (target - _simE5ccPV) * 0.05f; // heating/cooling lag
                
                // If Auto-Tune is running, simulate it oscillating slightly and then stopping
                if (_simE5ccAT == 1)
                {
                    _simE5ccPV += (float)(Math.Sin(DateTime.Now.Ticks / 10000000.0) * 0.5);
                    _simE5ccStatus = (ushort)((_simE5ccStatus & ~(1 << 0)) | (1 << 2)); // Stop = 0, AT = 1
                }
                else
                {
                    _simE5ccStatus = (ushort)(_simE5ccStatus & ~((1 << 0) | (1 << 2))); // Stop = 0, AT = 0
                }
            }
            else
            {
                _simE5ccPV += (25.0f - _simE5ccPV) * 0.01f; // cools down to room temp
                _simE5ccStatus = (ushort)((_simE5ccStatus | (1 << 0)) & ~(1 << 2)); // Stop = 1, AT = 0
            }

            // Check temp alarms
            if (_simE5ccPV >= _simAlm1 / 10.0f)
            {
                _simE5ccStatus |= (1 << 3); // trigger alarm 1
            }
            else
            {
                _simE5ccStatus &= unchecked((ushort)~(1 << 3));
            }
        }

        private ushort[] SimReadHolding(byte slave, ushort addr, ushort count)
        {
            ushort[] regs = new ushort[count];
            if (slave == 2) // Mixing Board
            {
                for (int i = 0; i < count; i++)
                {
                    int curr = addr + i;
                    if (curr == 20) regs[i] = _simRelay1;
                    else if (curr == 21) regs[i] = _simRelay2;
                    else if (curr >= 0 && curr < 48) // Config registers
                    {
                        int ch = curr / 8;
                        int param = curr % 8;
                        float val = 0;
                        if (param < 2) val = _simMinSccm[ch];
                        else if (param < 4) val = _simMaxSccm[ch];
                        else if (param < 6) val = _simMinV[ch];
                        else val = _simMaxV[ch];

                        ushort[] split = FloatToRegs(val);
                        regs[i] = (param % 2 == 0) ? split[0] : split[1];
                    }
                    else if (curr >= 60 && curr < 78) // Setpoint + DAC en
                    {
                        int ch = (curr - 60) / 3;
                        int param = (curr - 60) % 3;
                        if (param < 2)
                        {
                            ushort[] split = FloatToRegs(_simSccmSP[ch]);
                            regs[i] = (param == 0) ? split[0] : split[1];
                        }
                        else
                        {
                            regs[i] = _simDacEn[ch];
                        }
                    }
                }
            }
            else if (slave == 1) // E5CC
            {
                for (int i = 0; i < count; i++)
                {
                    int curr = addr + i;
                    if (curr == 0x0000) regs[i] = _simE5ccRunStop;
                    else if (curr == 0x0002) regs[i] = _simE5ccAT;
                    else if (curr == 0x0100) regs[i] = _simE5ccStatus;
                    else if (curr == 0x2000) regs[i] = (ushort)(_simE5ccPV * 10);
                    else if (curr == 0x2001) regs[i] = (ushort)(_simE5ccSP * 10);
                    else if (curr == 0x2002) regs[i] = (ushort)((_simE5ccRunStop == 0) ? 600 : 0); // simulated MV %
                    else if (curr == 0x2100) regs[i] = (ushort)(_simE5ccSP * 10);
                    else if (curr == 0x2200) regs[i] = _simAlm1;
                    else if (curr == 0x2201) regs[i] = _simAlm2;
                    else if (curr == 0x2300) regs[i] = _simP;
                    else if (curr == 0x2301) regs[i] = _simI;
                    else if (curr == 0x2302) regs[i] = _simD;
                    else if (curr == 0x2303) regs[i] = _simCtrlPeriod;
                    else if (curr == 0x2304) regs[i] = _simMvHi;
                    else if (curr == 0x2305) regs[i] = _simMvLo;
                    else if (curr == 0x2400) regs[i] = _simInputShift;
                    else if (curr == 0x2401) regs[i] = _simSpHi;
                    else if (curr == 0x2402) regs[i] = _simSpLo;
                }
            }
            return regs;
        }

        private ushort[] SimReadInput(byte slave, ushort addr, ushort count)
        {
            ushort[] regs = new ushort[count];
            if (slave == 2) // Mixing Board
            {
                for (int i = 0; i < count; i++)
                {
                    int curr = addr + i;
                    if (curr >= 0 && curr < 12) // Flow PV
                    {
                        int ch = curr / 2;
                        ushort[] split = FloatToRegs(_simSccmPV[ch]);
                        regs[i] = (curr % 2 == 0) ? split[0] : split[1];
                    }
                    else if (curr == 12)
                    {
                        regs[i] = 1; // dummy status flag
                    }
                }
            }
            return regs;
        }

        private void SimWriteSingle(byte slave, ushort addr, ushort val)
        {
            if (slave == 2)
            {
                if (addr == 20) _simRelay1 = val;
                else if (addr == 21) _simRelay2 = val;
                else if (addr >= 60 && addr < 78)
                {
                    int ch = (addr - 60) / 3;
                    int param = (addr - 60) % 3;
                    if (param == 2) _simDacEn[ch] = val;
                }
            }
            else if (slave == 1)
            {
                if (addr == 0x0000) _simE5ccRunStop = val;
                else if (addr == 0x0002)
                {
                    _simE5ccAT = val;
                    if (val == 1)
                    {
                        // Start simulation of Auto-Tune timing out after 10 seconds
                        ThreadPool.QueueUserWorkItem(_ =>
                        {
                            Thread.Sleep(10000);
                            _simE5ccAT = 0;
                            _simP = (ushort)(_rand.Next(80, 150));
                            _simI = (ushort)(_rand.Next(150, 300));
                            _simD = (ushort)(_rand.Next(30, 80));
                        });
                    }
                }
                else if (addr == 0x2100) _simE5ccSP = val / 10.0f;
                else if (addr == 0x2200) _simAlm1 = val;
                else if (addr == 0x2201) _simAlm2 = val;
                else if (addr == 0x2300) _simP = val;
                else if (addr == 0x2301) _simI = val;
                else if (addr == 0x2302) _simD = val;
                else if (addr == 0x2303) _simCtrlPeriod = val;
                else if (addr == 0x2304) _simMvHi = val;
                else if (addr == 0x2305) _simMvLo = val;
                else if (addr == 0x2400) _simInputShift = val;
                else if (addr == 0x2401) _simSpHi = val;
                else if (addr == 0x2402) _simSpLo = val;
            }
        }

        private void SimWriteMultiple(byte slave, ushort addr, ushort[] vals)
        {
            if (slave == 2)
            {
                for (int i = 0; i < vals.Length; i++)
                {
                    int curr = addr + i;
                    if (curr >= 0 && curr < 48) // config write
                    {
                        int ch = curr / 8;
                        int param = curr % 8;
                        if (param == 0 && vals.Length >= i + 2)
                        {
                            _simMinSccm[ch] = RegsToFloat(vals[i], vals[i+1]);
                        }
                        else if (param == 2 && vals.Length >= i + 2)
                        {
                            _simMaxSccm[ch] = RegsToFloat(vals[i], vals[i+1]);
                        }
                        else if (param == 4 && vals.Length >= i + 2)
                        {
                            _simMinV[ch] = RegsToFloat(vals[i], vals[i+1]);
                        }
                        else if (param == 6 && vals.Length >= i + 2)
                        {
                            _simMaxV[ch] = RegsToFloat(vals[i], vals[i+1]);
                        }
                    }
                    else if (curr >= 60 && curr < 78) // setpoint write
                    {
                        int ch = (curr - 60) / 3;
                        int param = (curr - 60) % 3;
                        if (param == 0 && vals.Length >= i + 2)
                        {
                            _simSccmSP[ch] = RegsToFloat(vals[i], vals[i+1]);
                        }
                    }
                }
            }
        }
    }
}
