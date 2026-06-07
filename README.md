# MOS Gas Mixer & Temperature Control System (WPF)

A commercial-grade native Windows SCADA application built in **C# WPF (.NET 10.0)** for the calibration, control, and real-time monitoring of multi-channel Mass Flow Controllers (MFCs) and temperature controllers.

Designed for industrial gas mixing applications, this software interfaces with a custom **Gas Mixing Control Board** (Slave 2) and an **Omron E5CC Temperature Controller** (Slave 1) using high-reliability **Modbus RTU** serial communication.

---

## 🚀 Key Features

* **Interactive SCADA Dashboard**:
  * Real-time pipeline animations reflecting gas flow status and routing.
  * Particle spraying animations depicting active chamber gas flows.
  * Automated vacuum exhaust animation linked to the pump state.
* **Dual Operating Modes**:
  * **Manual Mode**: Direct control over individual MFC target flows, valves, pumps, and temperature setpoints.
  * **Auto Mode (Scenario Execution)**: Builds and runs sequential step recipes.
* **AutoTable Recipe Builder**:
  * Dynamic calculation of dilution flow ranges based on Total Flow and source cylinder concentrations ($CO_1, CO_2, CO_3$).
  * Automatic step sequence generator with customizable Temperature, Concentration, Exposure, and Recovery durations.
  * Support for importing recipe lists from Excel (`.xlsx`) or CSV files.
* **High-Contrast Dark / Light Theme**:
  * Seamless application-wide theme toggling.
  * Dynamic styling optimized for high-visibility industrial control environments.
* **Advanced Hardware Calibration & Tuning**:
  * **MFC Range Settings**: Define individual full-scale flow ranges (sccm) and safety thresholds.
  * **MFC Voltage Calibration**: Dedicated calibration panel for Kofloc 3660 series controllers (analog $0 - 5\text{ VDC}$ voltage parameters in mV and correction factors) with factory default reset options.
  * **Omron E5CC PID Settings**: Real-time read/write for Proportional ($P$), Integral ($I$), and Derivative ($D$) terms, MV limits, and Auto-Tune command execution.
* **Real-time Charting & Data Logging**:
  * Real-time plot of actual gas flows (sccm) or concentrations (ppm).
  * Direct PNG image export of charts.
  * Continuous background logging of sensor values and flows to organized CSV logs.

---

## 📁 Repository Structure

```
MOS-Gas-Mixer-WPF/
├── docs/                      # Technical manuals and Modbus registers specifications
│   ├── Modbus_Communication_Guide_V2.pdf
│   └── Tap_lenh_Modbus_May_Tron_V2.xlsx
├── firmware/                  # Microcontroller target hex files
│   └── TronV41.hex
├── src/                       # WPF .NET C# Source Code
│   ├── App.xaml / App.xaml.cs
│   ├── MainWindow.xaml / MainWindow.xaml.cs
│   ├── AutoTableWindow.xaml / AutoTableWindow.xaml.cs
│   ├── MfcSettingWindow.xaml / MfcSettingWindow.xaml.cs
│   ├── MfcCalibWindow.xaml / MfcCalibWindow.xaml.cs
│   ├── MfcConfigWindow.xaml / MfcConfigWindow.xaml.cs
│   ├── E5ccPidWindow.xaml / E5ccPidWindow.xaml.cs
│   ├── ModbusConnWindow.xaml / ModbusConnWindow.xaml.cs
│   ├── ModbusHandler.cs
│   ├── PollingEngine.cs
│   ├── RecipeEngine.cs
│   ├── Logger.cs
│   └── Bo-Tron-Khi-CS.csproj
├── README.md                  # This developer integration guide
└── run.bat                    # Script to compile and run the application instantly
```

---

## 🎛 Modbus RTU Physical Layer Specification

The hardware controller expects a standard **Modbus RTU** frame layout over an RS-485 serial link:

| Parameter | Value | Note |
| :--- | :--- | :--- |
| **Interface** | RS-232 / RS-485 | Serial port interface |
| **Baud Rate** | 19,200 bps | Fixed baud rate for both controllers |
| **Data Bits** | 8 | Standard Modbus character length |
| **Parity** | Even (E) | Even parity bit |
| **Stop Bits** | 1 | 8E1 framing |
| **Modbus ID (Mixing Board)** | `0x02` (2) | Gas Mixing Control Board |
| **Modbus ID (Omron E5CC)** | `0x01` (1) | Omron E5CC Temperature Controller |

###Endianness
All 32-bit floating-point registers on the Mixing Board use **IEEE-754 Big-Endian Word** order. The High Word (sign + exponent) is stored in the lower register address, and the Low Word is stored in the higher register address.

---

## 🛠 Prerequisites

To run or build the application from source, make sure you have:
* **.NET SDK 10.0** or newer installed.
* A Windows machine (WPF runs natively on Windows 10 & 11).

---

## ⚡ Quick Start

Double-click the **`run.bat`** file in the root of this folder to compile and launch the application instantly.

Alternatively, you can run the following terminal commands:
```bash
# Navigate to source folder
cd src

# Restore dependencies and run the application
dotnet run
```

---

## ⚙ Simulation Mode

If no serial hardware is connected, check the **Simulation Mode** box in the Connection Setup dialog. The software features an internal simulation engine that models:
* Temperature heating/cooling curves matching active setpoints and Auto-Tuning timing.
* Mass Flow Controller (MFC) flows ramping up to target setpoints under realistic lag filters.
* Chamber pressure and pipelines reacting to Valve/Pump state toggling.
