using System;
using System.IO.Ports;
using System.Windows;

namespace Bo_Tron_Khi_CS
{
    public partial class ModbusConnWindow : Window
    {
        private readonly SystemConfig _config;

        public ModbusConnWindow(SystemConfig config)
        {
            InitializeComponent();
            _config = config;
            LoadCurrentSettings();
        }

        private void LoadCurrentSettings()
        {
            // Scan COM ports
            CbPorts.Items.Clear();
            CbPorts.Items.Add("Virtual Sim");
            string[] ports = SerialPort.GetPortNames();
            foreach (string port in ports)
            {
                CbPorts.Items.Add(port);
            }

            // Set selections
            if (CbPorts.Items.Contains(_config.port))
            {
                CbPorts.SelectedItem = _config.port;
            }
            else
            {
                CbPorts.SelectedIndex = 0; // Default to Sim
            }

            // Set baudrate
            string baudStr = _config.baudrate.ToString();
            for (int i = 0; i < CbBaud.Items.Count; i++)
            {
                if ((CbBaud.Items[i] as FrameworkElement).Tag?.ToString() == baudStr || 
                    (CbBaud.Items[i] as System.Windows.Controls.ComboBoxItem)?.Content.ToString() == baudStr)
                {
                    CbBaud.SelectedIndex = i;
                    break;
                }
            }

            // Set parity
            if (_config.parity == "O") CbParity.SelectedIndex = 1;
            else if (_config.parity == "N") CbParity.SelectedIndex = 2;
            else CbParity.SelectedIndex = 0;

            TxtTimeout.Text = _config.timeout.ToString("F2");
            TxtMixingSlave.Text = _config.mixing_slave.ToString();
            TxtE5ccSlave.Text = _config.e5cc_slave.ToString();
            ChkUseTcp.IsChecked = _config.port.StartsWith("TCP:"); // simple mapping or separate flag
        }

        private void OnSaveClick(object sender, RoutedEventArgs e)
        {
            try
            {
                _config.port = CbPorts.SelectedItem?.ToString() ?? "Virtual Sim";
                
                string baudText = (CbBaud.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content.ToString();
                if (int.TryParse(baudText, out int baud))
                {
                    _config.baudrate = baud;
                }

                string parityText = (CbParity.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content.ToString();
                if (parityText.Contains("Odd")) _config.parity = "O";
                else if (parityText.Contains("None")) _config.parity = "N";
                else _config.parity = "E";

                if (double.TryParse(TxtTimeout.Text, out double timeout))
                {
                    _config.timeout = timeout;
                }

                if (int.TryParse(TxtMixingSlave.Text, out int mixSlave))
                {
                    _config.mixing_slave = mixSlave;
                }

                if (int.TryParse(TxtE5ccSlave.Text, out int e5ccSlave))
                {
                    _config.e5cc_slave = e5ccSlave;
                }

                _config.simulation_mode = (_config.port == "Virtual Sim");

                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Invalid input values: {ex.Message}", "Input Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
