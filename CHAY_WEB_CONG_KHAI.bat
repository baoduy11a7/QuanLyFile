@echo off
chcp 65001 >nul
title PHÁT SÓNG HỆ THỐNG QUẢN LÝ HÓA ĐƠN QUA CLOUDFLARE TUNNEL
color 0B

echo ===============================================================================
echo      HỆ THỐNG QUẢN LÝ HÓA ĐƠN & KẾ TOÁN - KẾT NỐI TOÀN CẦU (CLOUDFLARE)
echo ===============================================================================
echo.
echo [*] Dang kiem tra ung dung Localhost (http://localhost:5249)...
echo [*] May chu chay tren mang Internet Viet Nam cua ban!
echo [*] Toc do cuc nhanh, khong bi Sleep, bao mat HTTPS toan cau!
echo.
echo ===============================================================================
echo [*] DUONG LINK TRUY CAP TRUC TUYEN CUA BAN SE HIEN THI PHIA DUOI (DANG trycloudflare.com):
echo ===============================================================================
echo.

if not exist "tools\cloudflared.exe" (
    echo [!] Chua tim thay file tools\cloudflared.exe. Vui long kiem tra lai.
    pause
    exit /b
)

tools\cloudflared.exe tunnel --url http://localhost:5249

echo.
pause
