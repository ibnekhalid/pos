using System.ComponentModel;
using System.Runtime.InteropServices;
using Restaurant.Client.Core.Platform;
using Restaurant.Client.Core.Printing;
using Restaurant.Client.UI.Services;

namespace Restaurant.Desktop.Platform;

/// <summary>
/// Receipts on the counter PC: raw ESC/POS straight to the register's Windows printer (fast, silent, cuts
/// the paper and kicks the cash drawer). With no printer set on the register it falls back to the print
/// dialog. Any thermal printer with a Windows driver works, including "Generic / Text Only". Urdu lines
/// print as raster images (<see cref="GdiTextRasterizer"/>).
/// </summary>
public class DesktopReceiptPrinter(HtmlReceiptPrinter dialog) : IReceiptPrinter
{
    public bool SupportsSilentPrinting => true;

    public async Task PrintAsync(PrintRequest request, CancellationToken cancellationToken = default)
    {
        if (request.PrinterName is null)
        {
            await dialog.PrintAsync(request, cancellationToken);
            return;
        }

        // Off the UI thread: Urdu lines are drawn as raster images first.
        await Task.Run(() =>
        {
            var bytes = EscPos.Render(request.Document, request.OpenCashDrawer, GdiTextRasterizer.Instance);
            WindowsRawPrinter.Send(request.PrinterName, $"Restaurant POS {request.Document.OrderNumber}", bytes);
        }, cancellationToken);
    }
}

/// <summary>Sends bytes unchanged to a Windows printer through the spooler (RAW data type).</summary>
public static class WindowsRawPrinter
{
    public static void Send(string printerName, string documentName, byte[] bytes)
    {
        if (!OpenPrinter(printerName, out var printer, IntPtr.Zero))
        {
            throw new InvalidOperationException($"Printer \"{printerName}\" was not found. Check the name in Settings → Branches → Registers.", new Win32Exception());
        }

        var unmanaged = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, unmanaged, bytes.Length);
            var doc = new DocInfo { Name = documentName, DataType = "RAW" };
            if (!StartDocPrinter(printer, 1, ref doc)) throw new Win32Exception();
            try
            {
                if (!StartPagePrinter(printer)) throw new Win32Exception();
                if (!WritePrinter(printer, unmanaged, bytes.Length, out var written) || written != bytes.Length) throw new Win32Exception();
                EndPagePrinter(printer);
            }
            finally
            {
                EndDocPrinter(printer);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(unmanaged);
            ClosePrinter(printer);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DocInfo
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string Name;
        [MarshalAs(UnmanagedType.LPWStr)] public string? OutputFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string DataType;
    }

    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool OpenPrinter(string name, out IntPtr printer, IntPtr defaults);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr printer);

    [DllImport("winspool.drv", EntryPoint = "StartDocPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool StartDocPrinter(IntPtr printer, int level, ref DocInfo info);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndDocPrinter(IntPtr printer);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool StartPagePrinter(IntPtr printer);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndPagePrinter(IntPtr printer);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool WritePrinter(IntPtr printer, IntPtr bytes, int count, out int written);
}
