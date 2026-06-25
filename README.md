# SonoPilot Modbus RTU — แอปควบคุมเครื่องเล่นเสียงผ่าน RS-485

แอปพลิเคชัน WPF .NET 8 สำหรับควบคุม **SonoPilot Lite** (RP2350 Pico 2) ผ่าน Modbus RTU บนสาย RS-485 — เหมาะกับงานติดตั้งที่เข้าถึง USB ไม่สะดวก

---

## คุณสมบัติ

- **Monitor แบบเรียลไทม์** — สถานะเล่น/หยุด, แทร็กปัจจุบัน, volume, อุณหภูมิ, uptime, sample rate, ชื่อไฟล์
- **ควบคุม** — Play / Stop / Next / Prev / Pause, ปรับ volume, repeat mode, mono, autoplay, goto track
- **Raw Register** — อ่าน/เขียน Modbus register โดยตรง (สำหรับ debug)
- **Baud rate ปรับได้** — 9600 / 19200 / 38400 / 57600 / 115200 / 230400 / 460800, "Set Device" เปลี่ยนทั้ง GUI + firmware พร้อมกัน
- **RS-485 RTS→DE** — รองรับ FTDI + RS-485 transceiver module (ETT Mini 422/485) โดย toggle RTS เป็นสัญญาณ DE
- **จำค่า** — COM port, slave ID, baud rate, RS-485 mode, ตำแหน่งหน้าต่าง
- **ธีม Catppuccin dark**

---

## ความต้องการ

- Windows 10/11
- .NET 8 Desktop Runtime
- USB-to-Serial adapter (FTDI232, CH340, CP2102 หรืออื่นๆ)
- RS-485 transceiver (ถ้าต่อสาย RS-485 จริง)

---

## การ build

```bash
dotnet build
dotnet run
```

หรือเปิด `SonoPilotModbus.sln` ใน Visual Studio 2022+

---

## การเชื่อมต่อ

### แบบที่ 1: UART ตรง (ไม่มี RS-485 transceiver)

ต่อ USB-Serial adapter ตรงเข้า Pico — ใช้ได้ระยะสั้น ไม่ต้องติ๊ก RS-485

```
FTDI TX  → Pico GP1 (Modbus RX)
FTDI RX  → Pico GP0 (Modbus TX)
FTDI GND → Pico GND
```

### แบบที่ 2: RS-485 ผ่าน transceiver (ETT Mini 422/485)

ติ๊ก **RS-485 (RTS→DE)** ในหน้า Connection — แอปจะ toggle RTS ของ FTDI เป็นสัญญาณ DE/RE ให้อัตโนมัติ

```
FTDI TX  → ETT TXD
FTDI RX  → ETT RXD
FTDI RTS → ETT DE/RE
FTDI GND → ETT GND
ETT A+   → Pico RS-485 A+
ETT B-   → Pico RS-485 B-
```

### แบบที่ 3: USB-to-RS485 adapter สำเร็จรูป

ใช้ adapter ที่มี DE ในตัว (เช่น USB-RS485-WE) — ไม่ต้องติ๊ก RS-485 เพราะ adapter จัดการ direction เอง

---

## Modbus Register Map

| Address | R/W | ความหมาย |
|---------|-----|---------|
| 0x0000 | RO | สถานะ (0=stop, 1=play, 3=pause) |
| 0x0001 | RO | แทร็กปัจจุบัน (0-based) |
| 0x0002 | RO | จำนวนแทร็กทั้งหมด |
| 0x0003 | RW | volume (0–100) |
| 0x0004 | RW | repeat mode (0=All, 1=One, 2=Off, 3=Single, 4=Random) |
| 0x0005 | RW | mono (0=stereo, 1=mono) |
| 0x0006 | RW | autoplay (0=off, 1=on) |
| 0x0007 | RO | SD card status (0=error, 1=OK) |
| 0x0010 | WO | คำสั่ง (1=play, 2=stop, 3=next, 4=prev, 5=pause) |
| 0x0011 | WO | goto track index |
| 0x0020 | RO | uptime (วินาที) |
| 0x0021 | RO | อุณหภูมิ × 10 (เช่น 271 = 27.1°C) |
| 0x0026 | RO | sample rate ÷ 100 (เช่น 441 = 44100 Hz) |
| 0x0027 | RW | baud index (0=9600, 1=19200, 2=38400, 3=57600, 4=115200, 5=230400, 6=460800) |
| 0x0100 | RO | ชื่อแทร็ก (16 registers = 32 ตัวอักษร) |

---

## พารามิเตอร์ Serial

| ค่า | ตั้งค่า |
|-----|--------|
| Baud rate | 9600 – 460800 (ค่าเริ่มต้น 115200, ปรับผ่าน GUI หรือ OLED) |
| Data bits | 8 |
| Parity | None |
| Stop bits | 1 |
| Slave ID | 1–247 (ตั้งผ่าน OLED เมนู Mbus หรือ GUI) |

---

## เฟิร์มแวร์

ใช้คู่กับ [SonoPilot Lite firmware](https://github.com/fildsady/rp2350-mp3-player) branch `feature/pcm5102a-i2s`

---

## ใบอนุญาต

MIT License
