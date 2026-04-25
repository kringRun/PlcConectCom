using System.IO.Ports;
using System.Text;

namespace PlcConectCom.WinFormsTest;

// 串口通讯服务
public sealed class SerialCommunicationService : IDisposable
{
    private SerialPort? _serialPort;

    public bool IsOpen => _serialPort?.IsOpen == true;

    public void Open(string portName, int baudRate = 9600, Parity parity = Parity.None, int dataBits = 8, StopBits stopBits = StopBits.One)
    {
        Close();
        _serialPort = new SerialPort(portName, baudRate, parity, dataBits, stopBits)
        {
            Encoding = Encoding.UTF8,
            ReadTimeout = 3000,
            WriteTimeout = 3000,
            NewLine = "\r\n"
        };
        _serialPort.Open();
    }

    public void Send(string data)
    {
        if (_serialPort is null || !_serialPort.IsOpen)
            throw new InvalidOperationException("串口未打开。");

        _serialPort.Write(data);
    }

    public string ReadExisting()
    {
        if (_serialPort is null || !_serialPort.IsOpen)
            throw new InvalidOperationException("串口未打开。");

        return _serialPort.ReadExisting();
    }

    public string ReadLine(int timeoutMs = 3000)
    {
        if (_serialPort is null || !_serialPort.IsOpen)
            throw new InvalidOperationException("串口未打开。");

        _serialPort.ReadTimeout = timeoutMs;
        return _serialPort.ReadLine();
    }

    public void Close()
    {
        if (_serialPort is null)
            return;

        try
        {
            if (_serialPort.IsOpen)
                _serialPort.Close();
        }
        catch
        {
            // ignore
        }
        finally
        {
            _serialPort.Dispose();
            _serialPort = null;
        }
    }

    public void Dispose()
    {
        Close();
        GC.SuppressFinalize(this);
    }
}
