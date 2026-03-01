# Classic Repair Toolbox

_Classic Repair Toolbox_ (or **CRT** hence forward) is a utility tool for repairing and diagnosing vintage computers and peripherals.

> [!CAUTION]
> Please note that this is currently in active DEVELOPMENT, and not all functionalities are working.

The project is a direct spin-off from my other project, [Commodore Repair Toolbox](https://github.com/HovKlan-DH/Commodore-Repair-Toolbox) and once the development finalizes, then the _Commodore Repair Toolbox_ project will get archived on GitHub and this new project will take over.

## Requirements

- .NET 10, https://dotnet.microsoft.com/en-us/download/dotnet/10.0
  - This is an LTS release (Long Time Stable) version
- 64-bit operating system:
  - **Windows** 10 build 1607 or newer (can it run on older versions with .NET 10???)
  - **macOS** 14 (Sonoma) or newer
  - **Linux**:
    - glibc 2.17+
    - OpenSSL 1.1+
    - libicu
    - Desktop:
      - X11
      - Wayland

## Compiling with Linux

Assuming some other dependencies exist, all it should be needed is to clone this repo, install dependencies

- sudo dnf install dotnet-runtime-10.0 dotnet-sdk-10.0

Run ```dotnet build``` for debug build
./bin/Debug/net10.0/Classic-Repair-Toolbox

or ```dotnet publish```  for production
./bin/Release/net10.0/Classic-Repair-Toolbox

## Confirmed working on

- Windows 10
- Windows 11
- ZorinOS
- RHEL 9.7
- macOS Intel (Tahoe)
- Fedora 43 x86_64
