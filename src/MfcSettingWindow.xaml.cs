using System;
using System.Windows;

namespace Bo_Tron_Khi_CS
{
    public partial class MfcSettingWindow : Window
    {
        private readonly SystemConfig _config;
        private readonly ModbusHandler _handler;

        public MfcSettingWindow(SystemConfig config, ModbusHandler handler)
        {
            InitializeComponent();
            _config = config;
            _handler = handler;
            LoadSettings();
        }

        private void LoadSettings()
        {
            if (_config.mfc_max_sccm != null && _config.mfc_max_sccm.Count >= 6)
            {
                TxtMfc1Range.Text = _config.mfc_max_sccm[0].ToString("F0");
                TxtMfc2Range.Text = _config.mfc_max_sccm[1].ToString("F0");
                TxtMfc3Range.Text = _config.mfc_max_sccm[2].ToString("F0");
                TxtMfc4Range.Text = _config.mfc_max_sccm[3].ToString("F0");
                TxtMfc5Range.Text = _config.mfc_max_sccm[4].ToString("F0");
                TxtMfc6Range.Text = _config.mfc_max_sccm[5].ToString("F0");
            }
            TxtULimit.Text = _config.u_limit_percent.ToString("F0");
            TxtLLimit.Text = _config.l_limit_percent.ToString("F0");
        }

        private void OnSaveClick(object sender, RoutedEventArgs e)
        {
            try
            {
                double[] ranges = new double[6];
                ranges[0] = double.Parse(TxtMfc1Range.Text);
                ranges[1] = double.Parse(TxtMfc2Range.Text);
                ranges[2] = double.Parse(TxtMfc3Range.Text);
                ranges[3] = double.Parse(TxtMfc4Range.Text);
                ranges[4] = double.Parse(TxtMfc5Range.Text);
                ranges[5] = double.Parse(TxtMfc6Range.Text);

                double uLimit = double.Parse(TxtULimit.Text);
                double lLimit = double.Parse(TxtLLimit.Text);

                // Save to local configuration
                for (int i = 0; i < 6; i++)
                {
                    _config.mfc_max_sccm[i] = ranges[i];
                }
                _config.u_limit_percent = uLimit;
                _config.l_limit_percent = lLimit;
                _config.Save();

                // Sync to device holding registers if connected
                if (_handler.IsConnected)
                {
                    byte ms = (byte)_config.mixing_slave;
                    for (int ch = 0; ch < 6; ch++)
                    {
                        float maxSccm = (float)_config.mfc_max_sccm[ch];
                        
                        // Write SP = 0 sccm
                        ushort[] zeroRegs = ModbusHandler.FloatToRegs(0.0f);
                        _handler.WriteMultipleRegisters(ms, (ushort)(60 + ch * 3), zeroRegs);

                        // Write min_sccm = 0
                        ushort[] minSccmRegs = ModbusHandler.FloatToRegs(0.0f);
                        _handler.WriteMultipleRegisters(ms, (ushort)(ch * 8), minSccmRegs);

                        // Write max_sccm = maxSccm
                        ushort[] maxSccmRegs = ModbusHandler.FloatToRegs(maxSccm);
                        _handler.WriteMultipleRegisters(ms, (ushort)(ch * 8 + 2), maxSccmRegs);
                    }
                    MessageBox.Show("MFC flow ranges updated and synced to device.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show("MFC flow ranges saved locally (simulation mode).", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }

                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Please enter valid numeric values.\nError: {ex.Message}", "Input Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
