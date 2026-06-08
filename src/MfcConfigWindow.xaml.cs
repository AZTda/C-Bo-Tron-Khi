using System;
using System.Windows;
using System.Windows.Controls;

namespace Bo_Tron_Khi_CS
{
    public partial class MfcConfigWindow : Window
    {
        private readonly SystemConfig _config;
        private readonly ModbusHandler _handler;

        public MfcConfigWindow(SystemConfig config, ModbusHandler handler)
        {
            InitializeComponent();
            _config = config;
            _handler = handler;
            LoadLocalSettings();
        }

        private void LoadLocalSettings()
        {
            // Populate fields from SystemConfig lists (assumes length >= 6)
            for (int ch = 1; ch <= 6; ch++)
            {
                int idx = ch - 1;
                GetFields(ch, out TextBox minS, out TextBox maxS, out TextBox minV, out TextBox maxV);
                
                // Note: local config holds min_sccm implicitly as 0.0, or we read from local configs
                minS.Text = "0.0"; 
                maxS.Text = _config.mfc_max_sccm[idx].ToString("F1");
                minV.Text = (_config.mfc_min_v[idx] / 1000.0f).ToString("F3");
                maxV.Text = (_config.mfc_max_v[idx] / 1000.0f).ToString("F3");
            }
        }

        private void GetFields(int ch, out TextBox minS, out TextBox maxS, out TextBox minV, out TextBox maxV)
        {
            minS = FindName($"TxtMinS_{ch}") as TextBox;
            maxS = FindName($"TxtMaxS_{ch}") as TextBox;
            minV = FindName($"TxtMinV_{ch}") as TextBox;
            maxV = FindName($"TxtMaxV_{ch}") as TextBox;
        }

        private void OnReadClick(object sender, RoutedEventArgs e)
        {
            if (!_handler.IsConnected)
            {
                MessageBox.Show("Modbus is not connected. Connect first.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Read 48 registers from address 0 using new TryRead API
            var result = _handler.TryReadHoldingRegisters((byte)_config.mixing_slave, 0, 48);
            if (result.Success && result.Data != null && result.Data.Length == 48)
            {
                for (int ch = 1; ch <= 6; ch++)
                {
                    int baseIdx = (ch - 1) * 8;
                    float minSccm = ModbusHandler.RegsToFloat(result.Data[baseIdx], result.Data[baseIdx + 1]);
                    float maxSccm = ModbusHandler.RegsToFloat(result.Data[baseIdx + 2], result.Data[baseIdx + 3]);
                    float minVolt = ModbusHandler.RegsToFloat(result.Data[baseIdx + 4], result.Data[baseIdx + 5]);
                    float maxVolt = ModbusHandler.RegsToFloat(result.Data[baseIdx + 6], result.Data[baseIdx + 7]);

                    GetFields(ch, out TextBox minS, out TextBox maxS, out TextBox minV, out TextBox maxV);
                    minS.Text = minSccm.ToString("F1");
                    maxS.Text = maxSccm.ToString("F1");
                    minV.Text = minVolt.ToString("F3");
                    maxV.Text = maxVolt.ToString("F3");
                }

                string retryInfo = result.RetryCount > 0 ? $" (retried {result.RetryCount}x)" : "";
                MessageBox.Show($"Read settings from device successfully!{retryInfo}", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show($"Failed to read device: {result.ErrorMessage}", "Modbus Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnWriteClick(object sender, RoutedEventArgs e)
        {
            try
            {
                // Parse values into Config and prepare register buffer
                ushort[] registers = new ushort[48];

                for (int ch = 1; ch <= 6; ch++)
                {
                    int idx = ch - 1;
                    GetFields(ch, out TextBox minS, out TextBox maxS, out TextBox minV, out TextBox maxV);

                    float minSccm = ParseUtil.ParseFloat(minS.Text);
                    float maxSccm = ParseUtil.ParseFloat(maxS.Text);
                    float minVolt = ParseUtil.ParseFloat(minV.Text);
                    float maxVolt = ParseUtil.ParseFloat(maxV.Text);

                    // Update local SystemConfig (note mV scaling for local config)
                    _config.mfc_max_sccm[idx] = maxSccm;
                    _config.mfc_min_v[idx] = (int)(minVolt * 1000);
                    _config.mfc_max_v[idx] = (int)(maxVolt * 1000);

                    // Split into Modbus float words
                    ushort[] w1 = ModbusHandler.FloatToRegs(minSccm);
                    ushort[] w2 = ModbusHandler.FloatToRegs(maxSccm);
                    ushort[] w3 = ModbusHandler.FloatToRegs(minVolt);
                    ushort[] w4 = ModbusHandler.FloatToRegs(maxVolt);

                    int baseIdx = idx * 8;
                    registers[baseIdx] = w1[0];
                    registers[baseIdx + 1] = w1[1];
                    registers[baseIdx + 2] = w2[0];
                    registers[baseIdx + 3] = w2[1];
                    registers[baseIdx + 4] = w3[0];
                    registers[baseIdx + 5] = w3[1];
                    registers[baseIdx + 6] = w4[0];
                    registers[baseIdx + 7] = w4[1];
                }

                _config.Save();

                if (_handler.IsConnected)
                {
                    byte slave = (byte)_config.mixing_slave;
                    var result = _handler.TryWriteMultipleRegisters(slave, 0, registers);
                    if (result.Success)
                    {
                        string retryInfo = result.RetryCount > 0 ? $" (retried {result.RetryCount}x)" : "";
                        MessageBox.Show($"Saved settings locally and wrote successfully to Mixing Board!{retryInfo}", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        MessageBox.Show($"Saved locally, but failed to write to device: {result.ErrorMessage}", "Partial Success", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
                else
                {
                    MessageBox.Show("Saved settings locally (simulation mode). Connect to Modbus to write to device.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save/write settings: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
