using System;
using System.Windows;

namespace Bo_Tron_Khi_CS
{
    public partial class E5ccPidWindow : Window
    {
        private readonly SystemConfig _config;
        private readonly ModbusHandler _handler;

        public E5ccPidWindow(SystemConfig config, ModbusHandler handler)
        {
            InitializeComponent();
            _config = config;
            _handler = handler;
            LoadLocalSettings();
        }

        private void LoadLocalSettings()
        {
            // Populate dialog with default / simulation settings
            TxtP.Text = "10.0";
            TxtI.Text = "240";
            TxtD.Text = "40.0";

            TxtSpMin.Text = "0.0";
            TxtSpMax.Text = "300.0";
            TxtMvMin.Text = "0.0";
            TxtMvMax.Text = "100.0";
            TxtInputShift.Text = "0.0";

            TxtAlm1.Text = _config.temp_alarm_limit.ToString("F1");
            TxtAlm2.Text = "50.0";
            TxtCtrlPeriod.Text = "12";
        }

        private void OnReadClick(object sender, RoutedEventArgs e)
        {
            if (!_handler.IsConnected)
            {
                MessageBox.Show("Modbus is not connected. Connect first.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            try
            {
                byte slave = (byte)_config.e5cc_slave;

                // 1. Read PID parameters (0x2300 - 0x2305)
                ushort[] pidData = _handler.ReadHoldingRegisters(slave, 0x2300, 6);
                if (pidData != null && pidData.Length >= 6)
                {
                    TxtP.Text = (pidData[0] / 10.0f).ToString("F1");
                    TxtI.Text = pidData[1].ToString();
                    TxtD.Text = (pidData[2] / 10.0f).ToString("F1");
                    TxtCtrlPeriod.Text = pidData[3].ToString();
                    TxtMvMax.Text = (pidData[4] / 10.0f).ToString("F1");
                    TxtMvMin.Text = (pidData[5] / 10.0f).ToString("F1");
                }

                // 2. Read SP limits (0x2400 - 0x2402)
                ushort[] spLimits = _handler.ReadHoldingRegisters(slave, 0x2400, 3);
                if (spLimits != null && spLimits.Length >= 3)
                {
                    TxtInputShift.Text = ((short)spLimits[0] / 10.0f).ToString("F1");
                    TxtSpMax.Text = ((short)spLimits[1] / 10.0f).ToString("F1");
                    TxtSpMin.Text = ((short)spLimits[2] / 10.0f).ToString("F1");
                }

                // 3. Read Alarm thresholds (0x2200 - 0x2202)
                ushort[] alarms = _handler.ReadHoldingRegisters(slave, 0x2200, 2);
                if (alarms != null && alarms.Length >= 2)
                {
                    TxtAlm1.Text = ((short)alarms[0] / 10.0f).ToString("F1");
                    TxtAlm2.Text = ((short)alarms[1] / 10.0f).ToString("F1");
                }

                MessageBox.Show("Read parameters from Omron E5CC successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to read controller: {ex.Message}", "Modbus Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnWriteClick(object sender, RoutedEventArgs e)
        {
            try
            {
                float p = ParseUtil.ParseFloat(TxtP.Text);
                ushort i = ParseUtil.ParseUshort(TxtI.Text);
                float d = ParseUtil.ParseFloat(TxtD.Text);
                ushort ctrlPeriod = ParseUtil.ParseUshort(TxtCtrlPeriod.Text);
                float mvMax = ParseUtil.ParseFloat(TxtMvMax.Text);
                float mvMin = ParseUtil.ParseFloat(TxtMvMin.Text);

                float spMin = ParseUtil.ParseFloat(TxtSpMin.Text);
                float spMax = ParseUtil.ParseFloat(TxtSpMax.Text);
                float offset = ParseUtil.ParseFloat(TxtInputShift.Text);

                float alm1 = ParseUtil.ParseFloat(TxtAlm1.Text);
                float alm2 = ParseUtil.ParseFloat(TxtAlm2.Text);

                // Update local SystemConfig
                _config.temp_alarm_limit = alm1;
                _config.Save();

                if (_handler.IsConnected)
                {
                    byte slave = (byte)_config.e5cc_slave;

                    // Write PID
                    _handler.WriteSingleRegister(slave, 0x2300, (ushort)(p * 10));
                    _handler.WriteSingleRegister(slave, 0x2301, i);
                    _handler.WriteSingleRegister(slave, 0x2302, (ushort)(d * 10));
                    _handler.WriteSingleRegister(slave, 0x2303, ctrlPeriod);
                    _handler.WriteSingleRegister(slave, 0x2304, (ushort)(mvMax * 10));
                    _handler.WriteSingleRegister(slave, 0x2305, (ushort)(mvMin * 10));

                    // Write Limits
                    _handler.WriteSingleRegister(slave, 0x2400, (ushort)(offset * 10));
                    _handler.WriteSingleRegister(slave, 0x2401, (ushort)(spMax * 10));
                    _handler.WriteSingleRegister(slave, 0x2402, (ushort)(spMin * 10));

                    // Write Alarms
                    _handler.WriteSingleRegister(slave, 0x2200, (ushort)(alm1 * 10));
                    _handler.WriteSingleRegister(slave, 0x2201, (ushort)(alm2 * 10));

                    MessageBox.Show("Saved settings locally and wrote successfully to Omron E5CC!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show("Saved settings locally (simulation mode). Connect to write to controller.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save/write settings: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnStartAtClick(object sender, RoutedEventArgs e)
        {
            if (!_handler.IsConnected)
            {
                MessageBox.Show("Modbus is not connected.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            try
            {
                byte slave = (byte)_config.e5cc_slave;
                _handler.WriteSingleRegister(slave, 0x0002, 1); // execute AT
                MessageBox.Show("Auto-Tune execution command sent!", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to start AT: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnStopAtClick(object sender, RoutedEventArgs e)
        {
            if (!_handler.IsConnected)
            {
                MessageBox.Show("Modbus is not connected.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            try
            {
                byte slave = (byte)_config.e5cc_slave;
                _handler.WriteSingleRegister(slave, 0x0002, 0); // cancel AT
                MessageBox.Show("Auto-Tune cancellation command sent!", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to stop AT: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
