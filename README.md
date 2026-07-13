# WinISO Builder

WinISO Builder là ứng dụng WPF dùng để tạo ISO cài đặt Windows tùy biến. Ứng dụng hỗ trợ đọc nguồn ISO Windows, giữ lại các edition được chọn, inject driver `.inf` offline bằng DISM, tùy chọn tạo unattended setup cho Lab Mode, và build lại ISO bootable bằng Windows ADK `oscdimg.exe`.

## Tính năng

- Chọn file Windows `.iso` hoặc thư mục source Windows đã giải nén.
- Đọc edition từ `sources\install.wim` hoặc `sources\install.esd`.
- Chỉ giữ lại các edition được chọn trong output image.
- Inject driver `.inf` đệ quy vào các edition đã chọn.
- Lab Mode tùy chọn cho môi trường test/internal.
- Log trực quan trong UI, bao gồm command đang chạy.
- Tự dọn `%TEMP%\WinISOBuilder` sau khi build, cancel, fail hoặc thoát app.
- Popup hoàn tất build có QR donate coffee.

## Yêu cầu

- Windows 10/11 x64.
- Chạy bằng quyền Administrator.
- Windows ADK Deployment Tools, bắt buộc có `oscdimg.exe`.
- DISM có sẵn trong Windows.
- .NET SDK 10 chỉ cần khi build từ source.

Cài nhanh môi trường:

```powershell
winget install -e --id Microsoft.WindowsADK
winget install -e --id Microsoft.DotNet.SDK.10
```

Đường dẫn `oscdimg.exe` thường gặp:

```text
C:\Program Files (x86)\Windows Kits\10\Assessment and Deployment Kit\Deployment Tools\amd64\Oscdimg\oscdimg.exe
```

## Build từ source

```powershell
dotnet restore
dotnet build WinISOBuilder.sln -c Release
```

## Publish

```powershell
dotnet publish WinISOBuilder.csproj -p:PublishProfile=win-x64
```

Output:

```text
bin\Release\net10.0-windows\publish\win-x64\WinISOBuilder.exe
```

## Cách sử dụng

1. Chạy `WinISOBuilder.exe` bằng quyền Administrator.
2. Chọn Windows ISO hoặc thư mục source đã giải nén.
3. Chọn các edition muốn giữ lại.
4. Chọn thư mục driver có chứa file `.inf`.
5. Chọn vị trí lưu ISO output.
6. Chỉ bật Lab Mode cho môi trường lab/internal đáng tin cậy.
7. Bấm `RUN AND BUILD`.

Nếu input là ISO, app sẽ giải nén vào:

```text
%TEMP%\WinISOBuilder\extract\<random>
```

Các file tạm sẽ được dọn tự động sau khi build xong hoặc khi thoát app.

## Hình ảnh

### 1. Giao diện chính

![Giao diện chính](asset/1.png)

### 2. Chọn ISO

![Chọn ISO](asset/2.png)

### 3. Unattended Setup

![Unattended Setup](asset/3.png)

### 4. Driver và vị trí lưu

![Driver và vị trí lưu](asset/4.png)

### 5. Xác nhận build

![Xác nhận build](asset/5.png)

### 6. Hoàn tất

![Hoàn tất](asset/6.png)

### 7. QR donate

<img src="asset/donate-qr.png" alt="QR donate" width="360">

## Lưu ý về Lab Mode

Lab Mode tạo local administrator account, bỏ qua một số màn hình OOBE, disable UAC, ngăn automatic device encryption, và ghi unattended setup files. Chỉ nên dùng cho lab, test bench hoặc môi trường internal đáng tin cậy.

## Troubleshooting

- `oscdimg.exe not found`: cài Windows ADK Deployment Tools.
- `Elevated permissions are required`: chạy lại app bằng quyền Administrator.
- Thư mục driver không có `.inf`: chọn đúng thư mục driver có thể quét đệ quy.
- Output ISO không boot được: kiểm tra source có đủ boot files.

Log kỹ thuật nằm tại:

```text
%TEMP%\WinISOBuilder\logs
```

## Autounattend.xml

File `Autounattend.xml` ở root repo là sample/reference. Khi bật Lab Mode, app sẽ tự generate `Autounattend.xml` riêng trong quá trình build.
