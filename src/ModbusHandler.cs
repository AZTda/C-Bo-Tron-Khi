using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Net.Sockets;
using System.Threading;

namespace Bo_Tron_Khi_CS
{
    // ===================================================
    // MODBUS RESULT — structured return instead of exceptions
    // ===================================================
    public class ModbusResult<T>
    {
        public bool Success { get; set; }
        public T Data { get; set; }
        public string ErrorMessage { get; set; }
        public int RetryCount { get; set; } // how many retries before success (0 = first try)

        public static ModbusResult<T> Ok(T data, int retries = 0) =>
            new ModbusResult<T> { Success = true, Data = data, RetryCount = retries };

        public static ModbusResult<T> Fail(string error, int retries = 0) =>
            new ModbusResult<T> { Success = false, ErrorMessage = error, RetryCount = retries };
    }

    // ===================================================
    // CONNECTION HEALTH EVENT
    // ===================================================
    public class ConnectionHealthEventArgs : EventArgs
    {
        public int ConsecutiveErrors { get; }
        public bool IsHealthy { get; }
        public string LastError { get; }
        public ConnectionHealthEventArgs(int errors, bool healthy, string lastErr)
        {
            ConsecutiveErrors = errors;
            IsHealthy = healthy;
            LastError = lastErr;
        }
    }

    // ===================================================
    // MODBUS HANDLER — Robust Communication Engine
    // ===================================================
    public class ModbusHandler
    {
        private SerialPort _serialPort;
        private TcpClient _tcpClient;
        private NetworkStream _tcpStream;
        private readonly object _lock = new object();
        private ushort _transactionId = 0;

        // RTU inter-frame timing
        private DateTime _lastTransactionEnd = DateTime.MinValue;
        private double _silentIntervalMs = 2.0; // 3.5 char times, recalculated on connect

        // Retry configuration
        public int MaxRetries { get; set; } = 3;
        private static readonly int[] BackoffMs = { 50, 100, 200 };

        // Health monitoring
        private int _consecutiveErrors = 0;
        private const int HealthThreshold = 5;
        public int ConsecutiveErrors => _consecutiveErrors;
        public event EventHandler<ConnectionHealthEventArgs> ConnectionHealthChanged;

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

        // ===================================================
        // CONNECTION MANAGEMENT
        // ===================================================
        public bool Connect()
        {
            lock (_lock)
            {
                Disconnect();
                if (Port == "Virtual Sim")
                {
                    IsConnected = true;
                    _consecutiveErrors = 0;
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
                        System.IO.Ports.Parity p = System.IO.Ports.Parity.Even;
                        if (Parity == "O") p = System.IO.Ports.Parity.Odd;
                        else if (Parity == "N") p = System.IO.Ports.Parity.None;

                        _serialPort = new SerialPort(Port, Baudrate, p, 8, StopBits.One)
                        {
                            ReadTimeout = (int)(Timeout * 1000),
                            WriteTimeout = (int)(Timeout * 1000)
                        };

                        _serialPort.Open();

                        // Calculate RTU inter-frame silent interval
                        // Modbus RTU: 3.5 character times, each char = 11 bits (start + 8data + parity + stop)
                        _silentIntervalMs = (3.5 * 11.0 / Baudrate) * 1000.0;
                        if (_silentIntervalMs < 1.75) _silentIntervalMs = 1.75; // minimum 1.75ms per Modbus spec for high baudrates
                    }

                    IsConnected = true;
                    _consecutiveErrors = 0;
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
                    if (_serialPort != null)
                    {
                        _serialPort.Close();
                    }
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

        // ===================================================
        // SYNCHRONOUS SERIAL RX
        // ===================================================
        /// <summary>
        /// Wait for at least 'count' bytes to arrive by polling the serial port.
        /// Returns the bytes or null on timeout/cancellation.
        /// </summary>
        private byte[] WaitForBytes(int count, int timeoutMs, CancellationToken ct)
        {
            byte[] result = new byte[count];
            int total = 0;
            var sw = Stopwatch.StartNew();

            while (total < count)
            {
                ct.ThrowIfCancellationRequested();

                if (sw.ElapsedMilliseconds >= timeoutMs)
                {
                    return null; // timeout
                }

                try
                {
                    if (_serialPort == null || !_serialPort.IsOpen)
                    {
                        return null;
                    }

                    int available = _serialPort.BytesToRead;
                    if (available > 0)
                    {
                        int toRead = Math.Min(available, count - total);
                        int read = _serialPort.Read(result, total, toRead);
                        if (read > 0)
                        {
                            total += read;
                        }
                    }
                    else
                    {
                        Thread.Sleep(2); // Yield CPU to prevent pinning
                    }
                }
                catch (Exception)
                {
                    return null;
                }
            }

            return result;
        }

        /// <summary>
        /// Read exactly 'count' bytes from TCP stream with timeout and cancellation.
        /// </summary>
        private byte[] TcpReadBytes(int count, int timeoutMs, CancellationToken ct)
        {
            byte[] buffer = new byte[count];
            int total = 0;
            var sw = Stopwatch.StartNew();

            while (total < count && sw.ElapsedMilliseconds < timeoutMs)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    int read = _tcpStream.Read(buffer, total, count - total);
                    if (read <= 0) break;
                    total += read;
                }
                catch (IOException) { break; }
            }

            return total >= count ? buffer : null;
        }

        // ===================================================
        // RTU INTER-FRAME TIMING
        // ===================================================
        private void EnforceInterFrameDelay()
        {
            if (IsTcp) return; // TCP/IP doesn't need inter-frame delay

            double elapsed = (DateTime.Now - _lastTransactionEnd).TotalMilliseconds;
            if (elapsed < _silentIntervalMs)
            {
                int waitMs = (int)Math.Ceiling(_silentIntervalMs - elapsed);
                if (waitMs > 0) Thread.Sleep(waitMs);
            }
        }

        // ===================================================
        // PUBLIC API — New (ModbusResult<T>)
        // ===================================================
        public ModbusResult<ushort[]> TryReadHoldingRegisters(byte slave, ushort startAddress, ushort count, CancellationToken ct = default)
        {
            if (Port == "Virtual Sim")
            {
                UpdateSimulation();
                return ModbusResult<ushort[]>.Ok(SimReadHolding(slave, startAddress, count));
            }
            return PerformReadTransaction(slave, 0x03, startAddress, count, ct);
        }

        public ModbusResult<ushort[]> TryReadInputRegisters(byte slave, ushort startAddress, ushort count, CancellationToken ct = default)
        {
            if (Port == "Virtual Sim")
            {
                UpdateSimulation();
                return ModbusResult<ushort[]>.Ok(SimReadInput(slave, startAddress, count));
            }
            return PerformReadTransaction(slave, 0x04, startAddress, count, ct);
        }

        public ModbusResult<bool> TryWriteSingleRegister(byte slave, ushort address, ushort value, CancellationToken ct = default)
        {
            if (Port == "Virtual Sim")
            {
                SimWriteSingle(slave, address, value);
                return ModbusResult<bool>.Ok(true);
            }
            return PerformWriteTransaction(slave, 0x06, address, value, null, ct);
        }

        public ModbusResult<bool> TryWriteMultipleRegisters(byte slave, ushort startAddress, ushort[] values, CancellationToken ct = default)
        {
            if (Port == "Virtual Sim")
            {
                SimWriteMultiple(slave, startAddress, values);
                return ModbusResult<bool>.Ok(true);
            }
            return PerformWriteTransaction(slave, 0x10, startAddress, (ushort)values.Length, values, ct);
        }

        // ===================================================
        // PUBLIC API — Legacy (backward-compatible wrappers)
        // These throw exceptions on failure like the old API
        // ===================================================
        public ushort[] ReadHoldingRegisters(byte slave, ushort startAddress, ushort count)
        {
            var result = TryReadHoldingRegisters(slave, startAddress, count);
            if (!result.Success) throw new IOException(result.ErrorMessage);
            return result.Data;
        }

        public ushort[] ReadInputRegisters(byte slave, ushort startAddress, ushort count)
        {
            var result = TryReadInputRegisters(slave, startAddress, count);
            if (!result.Success) throw new IOException(result.ErrorMessage);
            return result.Data;
        }

        public void WriteSingleRegister(byte slave, ushort address, ushort value)
        {
            var result = TryWriteSingleRegister(slave, address, value);
            if (!result.Success) throw new IOException(result.ErrorMessage);
        }

        public void WriteMultipleRegisters(byte slave, ushort startAddress, ushort[] values)
        {
            var result = TryWriteMultipleRegisters(slave, startAddress, values);
            if (!result.Success) throw new IOException(result.ErrorMessage);
        }

        // ===================================================
        // CORE TRANSACTION ENGINE — with retry + event-driven RX
        // ===================================================
        private ModbusResult<ushort[]> PerformReadTransaction(byte slave, byte fc, ushort addr, ushort count, CancellationToken ct)
        {
            string lastError = null;

            for (int attempt = 0; attempt <= MaxRetries; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                // Backoff delay before retry (not on first attempt)
                if (attempt > 0)
                {
                    int backoff = (attempt - 1 < BackoffMs.Length) ? BackoffMs[attempt - 1] : BackoffMs[BackoffMs.Length - 1];
                    Thread.Sleep(backoff);
                }

                lock (_lock)
                {
                    if (!IsConnected)
                    {
                        lastError = "Not connected to Modbus";
                        continue;
                    }

                    try
                    {
                        // 1. Enforce RTU inter-frame silence
                        EnforceInterFrameDelay();

                        // 2. Build request
                        byte[] request = BuildReadRequest(slave, fc, addr, count);

                        // 3. Flush RX buffer and send
                        FlushRxBuffer();
                        SendRequest(request);

                        // 4. Wait for response (event-driven, not blocking)
                        int timeoutMs = (int)(Timeout * 1000);
                        ushort[] registers = ReceiveReadResponse(slave, fc, count, timeoutMs, ct);

                        // 5. Success — update health and return
                        _lastTransactionEnd = DateTime.Now;
                        RecordSuccess();
                        return ModbusResult<ushort[]>.Ok(registers, attempt);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        lastError = ex.Message;
                        _lastTransactionEnd = DateTime.Now;
                    }
                }
            }

            // All retries exhausted
            RecordError(lastError);
            return ModbusResult<ushort[]>.Fail($"Read failed after {MaxRetries + 1} attempts: {lastError}", MaxRetries);
        }

        private ModbusResult<bool> PerformWriteTransaction(byte slave, byte fc, ushort addrOrQty, ushort countOrValue, ushort[] writeValues, CancellationToken ct)
        {
            string lastError = null;

            for (int attempt = 0; attempt <= MaxRetries; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                if (attempt > 0)
                {
                    int backoff = (attempt - 1 < BackoffMs.Length) ? BackoffMs[attempt - 1] : BackoffMs[BackoffMs.Length - 1];
                    Thread.Sleep(backoff);
                }

                lock (_lock)
                {
                    if (!IsConnected)
                    {
                        lastError = "Not connected to Modbus";
                        continue;
                    }

                    try
                    {
                        EnforceInterFrameDelay();

                        byte[] request = BuildWriteRequest(slave, fc, addrOrQty, countOrValue, writeValues);

                        FlushRxBuffer();
                        SendRequest(request);

                        int timeoutMs = (int)(Timeout * 1000);
                        ReceiveWriteResponse(slave, fc, timeoutMs, ct);

                        _lastTransactionEnd = DateTime.Now;
                        RecordSuccess();
                        return ModbusResult<bool>.Ok(true, attempt);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        lastError = ex.Message;
                        _lastTransactionEnd = DateTime.Now;
                    }
                }
            }

            RecordError(lastError);
            return ModbusResult<bool>.Fail($"Write failed after {MaxRetries + 1} attempts: {lastError}", MaxRetries);
        }

        // ===================================================
        // REQUEST BUILDERS
        // ===================================================
        private byte[] BuildReadRequest(byte slave, byte fc, ushort addr, ushort count)
        {
            if (IsTcp)
            {
                _transactionId++;
                byte[] request = new byte[12];
                request[0] = (byte)(_transactionId >> 8);
                request[1] = (byte)(_transactionId & 0xFF);
                request[2] = 0; request[3] = 0; // Protocol ID
                request[4] = 0; request[5] = 6; // Length
                request[6] = slave;
                request[7] = fc;
                request[8] = (byte)(addr >> 8);
                request[9] = (byte)(addr & 0xFF);
                request[10] = (byte)(count >> 8);
                request[11] = (byte)(count & 0xFF);
                return request;
            }
            else
            {
                byte[] request = new byte[8];
                request[0] = slave;
                request[1] = fc;
                request[2] = (byte)(addr >> 8);
                request[3] = (byte)(addr & 0xFF);
                request[4] = (byte)(count >> 8);
                request[5] = (byte)(count & 0xFF);
                ushort crc = CalculateCRC(request, 6);
                request[6] = (byte)(crc & 0xFF);
                request[7] = (byte)(crc >> 8);
                return request;
            }
        }

        private byte[] BuildWriteRequest(byte slave, byte fc, ushort addr, ushort countOrValue, ushort[] writeValues)
        {
            if (IsTcp)
            {
                _transactionId++;
                int pduLen = (fc == 0x10) ? (7 + writeValues.Length * 2) : 6;
                byte[] request = new byte[6 + pduLen];

                // MBAP Header
                request[0] = (byte)(_transactionId >> 8);
                request[1] = (byte)(_transactionId & 0xFF);
                request[2] = 0; request[3] = 0;
                request[4] = (byte)(pduLen >> 8);
                request[5] = (byte)(pduLen & 0xFF);
                request[6] = slave;
                request[7] = fc;
                request[8] = (byte)(addr >> 8);
                request[9] = (byte)(addr & 0xFF);
                request[10] = (byte)(countOrValue >> 8);
                request[11] = (byte)(countOrValue & 0xFF);

                if (fc == 0x10)
                {
                    request[12] = (byte)(writeValues.Length * 2);
                    for (int i = 0; i < writeValues.Length; i++)
                    {
                        request[13 + i * 2] = (byte)(writeValues[i] >> 8);
                        request[14 + i * 2] = (byte)(writeValues[i] & 0xFF);
                    }
                }
                return request;
            }
            else
            {
                int reqLen = (fc == 0x10) ? (9 + writeValues.Length * 2) : 8;
                byte[] request = new byte[reqLen];
                request[0] = slave;
                request[1] = fc;
                request[2] = (byte)(addr >> 8);
                request[3] = (byte)(addr & 0xFF);
                request[4] = (byte)(countOrValue >> 8);
                request[5] = (byte)(countOrValue & 0xFF);

                if (fc == 0x10)
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
                return request;
            }
        }

        // ===================================================
        // SEND + RECEIVE (event-driven)
        // ===================================================
        private void FlushRxBuffer()
        {
            if (IsTcp) return;
            try { _serialPort?.DiscardInBuffer(); } catch { }
        }

        private void SendRequest(byte[] request)
        {
            if (IsTcp)
            {
                _tcpStream.Write(request, 0, request.Length);
            }
            else
            {
                _serialPort.Write(request, 0, request.Length);
            }
        }

        private ushort[] ReceiveReadResponse(byte slave, byte fc, ushort expectedCount, int timeoutMs, CancellationToken ct)
        {
            if (IsTcp)
            {
                // TCP: MBAP header (7) + FC (1) + byteCount (1) = 9 bytes minimum
                byte[] header = TcpReadBytes(9, timeoutMs, ct);
                if (header == null) throw new IOException("Timeout reading TCP Modbus response header");

                byte responseFC = header[7];
                if ((responseFC & 0x80) != 0)
                    throw new Exception($"Modbus Exception: 0x{header[8]:X2}");

                byte byteCount = header[8];
                byte[] data = TcpReadBytes(byteCount, timeoutMs, ct);
                if (data == null) throw new IOException("Timeout reading TCP Modbus response data");

                ushort[] registers = new ushort[byteCount / 2];
                for (int i = 0; i < registers.Length; i++)
                    registers[i] = (ushort)((data[i * 2] << 8) | data[i * 2 + 1]);
                return registers;
            }
            else
            {
                // RTU: slave(1) + FC(1) + byteCount(1) = 3 header bytes
                byte[] header = WaitForBytes(3, timeoutMs, ct);
                if (header == null) throw new IOException("Timeout waiting for RTU response header (no data from device)");

                if (header[0] != slave)
                    throw new IOException($"Slave address mismatch: expected {slave}, got {header[0]}");

                if ((header[1] & 0x80) != 0)
                {
                    // Exception response: need 2 more bytes (error code + CRC)
                    byte[] errTail = WaitForBytes(2, timeoutMs, ct);
                    throw new Exception($"Modbus Exception: 0x{header[2]:X2}");
                }

                byte byteCount = header[2];
                // Data bytes + 2 CRC bytes
                byte[] dataPlusCrc = WaitForBytes(byteCount + 2, timeoutMs, ct);
                if (dataPlusCrc == null) throw new IOException("Timeout waiting for RTU response data");

                // Validate CRC over entire response
                byte[] fullResponse = new byte[3 + byteCount + 2];
                Buffer.BlockCopy(header, 0, fullResponse, 0, 3);
                Buffer.BlockCopy(dataPlusCrc, 0, fullResponse, 3, byteCount + 2);

                ushort receivedCrc = (ushort)(dataPlusCrc[byteCount] | (dataPlusCrc[byteCount + 1] << 8));
                ushort calculatedCrc = CalculateCRC(fullResponse, fullResponse.Length - 2);
                if (receivedCrc != calculatedCrc)
                    throw new IOException($"CRC mismatch: received 0x{receivedCrc:X4}, calculated 0x{calculatedCrc:X4}");

                ushort[] registers = new ushort[byteCount / 2];
                for (int i = 0; i < registers.Length; i++)
                    registers[i] = (ushort)((dataPlusCrc[i * 2] << 8) | dataPlusCrc[i * 2 + 1]);
                return registers;
            }
        }

        private void ReceiveWriteResponse(byte slave, byte fc, int timeoutMs, CancellationToken ct)
        {
            if (IsTcp)
            {
                // TCP write response: MBAP(7) + FC(1) + addr(2) + qty/val(2) = 12 bytes
                byte[] resp = TcpReadBytes(12, timeoutMs, ct);
                if (resp == null) throw new IOException("Timeout reading TCP write response");

                if ((resp[7] & 0x80) != 0)
                    throw new Exception($"Modbus Exception: 0x{resp[8]:X2}");
            }
            else
            {
                // RTU write response: slave(1) + FC(1) + addr(2) + val/qty(2) + CRC(2) = 8 bytes
                byte[] resp = WaitForBytes(8, timeoutMs, ct);
                if (resp == null) throw new IOException("Timeout waiting for RTU write response");

                if (resp[0] != slave)
                    throw new IOException($"Slave address mismatch: expected {slave}, got {resp[0]}");

                if ((resp[1] & 0x80) != 0)
                    throw new Exception($"Modbus Exception: 0x{resp[2]:X2}");

                // Validate CRC
                ushort receivedCrc = (ushort)(resp[6] | (resp[7] << 8));
                ushort calculatedCrc = CalculateCRC(resp, 6);
                if (receivedCrc != calculatedCrc)
                    throw new IOException($"CRC mismatch in write response");
            }
        }

        // ===================================================
        // HEALTH MONITORING
        // ===================================================
        private void RecordSuccess()
        {
            if (_consecutiveErrors > 0)
            {
                _consecutiveErrors = 0;
                ConnectionHealthChanged?.Invoke(this, new ConnectionHealthEventArgs(0, true, null));
            }
        }

        private void RecordError(string error)
        {
            _consecutiveErrors++;
            bool wasHealthy = _consecutiveErrors <= HealthThreshold;
            ConnectionHealthChanged?.Invoke(this, new ConnectionHealthEventArgs(_consecutiveErrors, _consecutiveErrors < HealthThreshold, error));
        }

        // ===================================================
        // UTILITIES
        // ===================================================
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
                        else if (param == 2)
                        {
                            _simDacEn[ch] = vals[i];
                        }
                    }
                }
            }
        }
    }
}
