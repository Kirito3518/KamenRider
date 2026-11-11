using System.Runtime.InteropServices;

namespace KamenRaider.AgentMaintenance;

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct WindowsFileTime
{
    public uint Low;
    public uint High;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct WindowsFileInformation
{
    public uint Attributes;
    public WindowsFileTime CreationTime;
    public WindowsFileTime LastAccessTime;
    public WindowsFileTime LastWriteTime;
    public uint VolumeSerialNumber;
    public uint FileSizeHigh;
    public uint FileSizeLow;
    public uint NumberOfLinks;
    public uint FileIndexHigh;
    public uint FileIndexLow;
}
