using System;
using System.IO;
using System.IO.Ports;

namespace SonoPilotModbus;

/// <summary>
/// Wraps a SerialPort stream for STM32 Bridge mode.
/// TX: wraps outgoing bytes in [0x03, len, data...]
/// RX: unwraps incoming [0x04, len, data...] → raw Modbus response
/// </summary>
public class BridgeStream : Stream
{
    private readonly SerialPort _port;
    private readonly byte[] _rxBuf = new byte[256];
    private int _rxPos, _rxLen;

    public BridgeStream(SerialPort port) { _port = port; }

    public override void Write(byte[] buffer, int offset, int count)
    {
        if (count <= 0 || count > 250) return;
        byte[] pkt = new byte[2 + count];
        pkt[0] = 0x03; // BRIDGE_CMD_MODBUS_TX
        pkt[1] = (byte)count;
        Array.Copy(buffer, offset, pkt, 2, count);
        _port.BaseStream.Write(pkt, 0, pkt.Length);
        _port.BaseStream.Flush();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        // Return buffered data first
        if (_rxPos < _rxLen)
        {
            int avail = Math.Min(count, _rxLen - _rxPos);
            Array.Copy(_rxBuf, _rxPos, buffer, offset, avail);
            _rxPos += avail;
            return avail;
        }

        // Read bridge frame: [0x04, len, data...]
        int timeout = _port.ReadTimeout;
        var start = DateTime.Now;

        while (true)
        {
            if ((DateTime.Now - start).TotalMilliseconds > timeout)
                throw new TimeoutException("Bridge read timeout");

            if (_port.BytesToRead < 1) { System.Threading.Thread.Sleep(1); continue; }

            int cmd = _port.BaseStream.ReadByte();
            if (cmd != 0x04) continue; // skip non-Modbus frames

            // Wait for length byte
            while (_port.BytesToRead < 1)
            {
                if ((DateTime.Now - start).TotalMilliseconds > timeout)
                    throw new TimeoutException("Bridge read timeout (len)");
                System.Threading.Thread.Sleep(1);
            }

            int len = _port.BaseStream.ReadByte();
            if (len <= 0 || len > 250) continue;

            // Read payload
            int read = 0;
            while (read < len)
            {
                if ((DateTime.Now - start).TotalMilliseconds > timeout)
                    throw new TimeoutException("Bridge read timeout (data)");
                if (_port.BytesToRead > 0)
                {
                    int n = _port.BaseStream.Read(_rxBuf, read, len - read);
                    read += n;
                }
                else System.Threading.Thread.Sleep(1);
            }

            _rxPos = 0;
            _rxLen = len;
            int ret = Math.Min(count, _rxLen);
            Array.Copy(_rxBuf, 0, buffer, offset, ret);
            _rxPos = ret;
            return ret;
        }
    }

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => _port.BaseStream.Flush();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
