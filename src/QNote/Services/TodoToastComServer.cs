using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using QNote.Infrastructure;
using Windows.UI.Notifications;

namespace QNote.Services;

/// <summary>
/// Hand-rolled COM toast-activation server for todo reminders (task 10-04,
/// AOT-safe execution layer). Replaces the Toolkit's
/// <c>ToastNotificationManagerCompat</c> activator, which synthesizes its COM
/// class at RUNTIME via <c>Reflection.Emit</c> (<c>CreateActivatorType</c>) —
/// unsupported under NativeAOT: the IL3053/IL2104 publish errors are real (the
/// emitted activator type cannot exist in an AOT image, so click activation
/// would throw), and warnings must NOT be suppressed (repo rule). The old
/// desktop-compat API (<c>DesktopNotificationManagerCompat</c>) is Emit-free
/// at the C# level (verified against the 7.1.3 package source) but still
/// fails the analyzer gate: its <c>CoRegisterClassObject</c> P/Invoke declares
/// an <c>[MarshalAs(IUnknown)] object</c> parameter → IL2050 rolls up into the
/// IL2104 assembly-level trim error under <c>TreatWarningsAsErrors</c>.
///
/// This file therefore implements the SAME choreography the old API documented
/// — concrete activator class + <c>CoRegisterClassObject</c> (warm clicks) +
/// <c>LocalServer32</c> registry (cold clicks launch <c>"&lt;exe&gt;" -ToastActivated</c>)
/// + AUMID registration for the unpackaged channel — with the modern
/// source-generated COM interop (<c>[GeneratedComInterface]</c> /
/// <c>[GeneratedComClass]</c>): the CCW vtables are emitted by the C# compiler
/// (unsafe function pointers), so the whole surface is reflection- and
/// Emit-free and provably AOT-clean (validated standalone on the AOT publish
/// + runtime path before porting; see the stage-③ AOT fix report).
///
/// Channel wiring:
/// <list type="bullet">
/// <item><b>Packaged (Store/MSIX)</b>: package identity supplies the AUMID
/// (no registry needed — the AppUserModelId comes from the manifest); the
/// cold-launch path is the <c>com:ExeServer</c> declaration in
/// <c>Package.appxmanifest</c> (CLSID must match <see cref="ActivatorClsid"/>).</item>
/// <item><b>Unpackaged (portable)</b>: a fixed, folder-independent AUMID
/// (<see cref="Aumid"/>) is registered under
/// <c>HKCU\Software\Classes\AppUserModelId</c> with the display name, icon and
/// <c>CustomActivator</c> CLSID; the CLSID's <c>LocalServer32</c> points at the
/// CURRENT exe. Both keys are rewritten on every launch, so a moved portable
/// folder self-heals (same philosophy as <c>HealStartupRunValueAsync</c>).</item>
/// </list>
/// Everything here is best-effort: failures are logged (Warning) and
/// swallowed — scheduling keeps working when activation registration fails
/// (clicks then degrade to plain launches), and the app always comes up.
/// </summary>
internal static partial class TodoToastComServer
{
    /// <summary>
    /// Fixed AUMID for the unpackaged channel. Deliberately independent of the
    /// exe path so scheduled toasts survive portable-folder moves (a
    /// path-hashed AUMID would strand the OS schedule of every move, exactly
    /// the failure the reconcile's 14-day fill guards against).
    /// </summary>
    public const string Aumid = "QNote.Portable";

    /// <summary>
    /// CLSID of <see cref="TodoNotificationActivator"/> — shared by the
    /// <c>LocalServer32</c> / <c>CustomActivator</c> registry writes (unpackaged)
    /// and the <c>com:ExeServer</c> manifest declaration (packaged). Frozen:
    /// changing it orphans every existing registration.
    /// </summary>
    public static readonly Guid ActivatorClsid = new("0D206123-A80D-4247-B145-2C10566F6DAF");

    /// <summary>IID of <see cref="INotificationActivationCallback"/> (the OS contract).</summary>
    private static readonly Guid CallbackIid = new("53E31837-6600-4A81-9395-75CFFE746F94");

    /// <summary>IID_IUnknown.</summary>
    private static readonly Guid IidIUnknown = new("00000000-0000-0000-C000-000000000046");

    /// <summary>CoRegisterClassObject context: out-of-process local server.</summary>
    private const uint ClsctxLocalServer = 4;

    /// <summary>CoRegisterClassObject flags: one class object serves multiple callers — COM reuses the running process instead of launching a new one for warm clicks.</summary>
    private const uint RegclsMultipleUse = 1;

    private static readonly ComWrappers Wrappers = new StrategyBasedComWrappers();

    /// <summary>Static root keeping the registered factory alive (belt-and-braces; COM also holds a ref).</summary>
    private static ToastActivatorClassFactory? _factory;

    private static int _registered;

    /// <summary>
    /// Whether the OS can route toast clicks to us: the in-process class
    /// object is registered (warm path). The cold path additionally needs the
    /// registry / manifest wiring above, which <see cref="Register"/> writes
    /// best-effort alongside.
    /// </summary>
    public static bool IsRegistered => Volatile.Read(ref _registered) != 0;

    /// <summary>
    /// Idempotent startup registration — call once from the app's initial
    /// startup code (App ctor), BEFORE any toast is scheduled or clicked.
    /// <c>CoRegisterClassObject</c> makes the running process serve warm
    /// clicks (REGCLS_MULTIPLEUSE: COM reuses the registered class object
    /// instead of launching a second process); the registry/manifest wiring
    /// covers the cold path (app closed → OS launches the exe as a COM local
    /// server with <c>-ToastActivated</c> on the command line, the activation
    /// is delivered once <see cref="Register"/> has run). Never throws.
    /// </summary>
    public static void Register(ILogger log)
    {
        if (Interlocked.Exchange(ref _registered, 1) != 0)
            return; // already registered this session

        try
        {
            // Registry choreography — unpackaged channel only. Packaged runs
            // get their AUMID from package identity and their activator from
            // the manifest com:ExeServer; registry writes are skipped (MSIX
            // virtualizes HKCU anyway).
            if (!QNote.PackageIdentity.IsPackaged())
            {
                var exePath = Environment.ProcessPath;
                if (exePath is not null)
                {
                    // CLSID → LocalServer32: how the OS cold-launches the exe
                    // as a COM server. Rewritten every launch — a moved
                    // portable folder self-heals on the next start.
                    using (var clsid = Registry.CurrentUser.CreateSubKey(
                        $@"Software\Classes\CLSID\{{{ActivatorClsid:D}}}\LocalServer32"))
                    {
                        clsid.SetValue(null, $"\"{exePath}\" -ToastActivated");
                    }

                    // AUMID → display identity + activator for an app with no
                    // Start-menu shortcut (the Win10 1903+ registry form; same
                    // fields ToastNotificationManagerCompat writes).
                    using (var aumidKey = Registry.CurrentUser.CreateSubKey(
                        $@"Software\Classes\AppUserModelId\{Aumid}"))
                    {
                        aumidKey.SetValue("DisplayName", "QNote");
                        aumidKey.SetValue("CustomActivator", $"{{{ActivatorClsid:D}}}");
                        var icon = Path.Combine(PortableDataRoot.ExeDirectory(), "Assets", "AppIcon.ico");
                        if (File.Exists(icon))
                            aumidKey.SetValue("IconUri", new Uri(icon).AbsoluteUri);
                        aumidKey.SetValue("IconBackgroundColor", "FFDDDDDD");

                        // Identity-less AUMID warm-up (mirrors the Compat's
                        // PreRegisterIdentityLessApp, verified the hard way in
                        // the stage-③ AOT smoke): Windows silently drops
                        // notifications from an unpackaged AUMID that has
                        // never sent a toast, INCLUDING scheduled ones — seed
                        // the OS state once with a suppressed, 15-second,
                        // immediately-removed warm-up toast. The persistent
                        // HasSentNotification marker uses the same value name
                        // the Compat builds wrote, so machines that already
                        // ran a Compat-based QNote skip this.
                        if (aumidKey.GetValue("HasSentNotification") is null)
                        {
                            var xml = new Windows.Data.Xml.Dom.XmlDocument();
                            xml.LoadXml("""
                                <toast><visual><binding template="ToastGeneric"><text>QNote</text></binding></visual></toast>
                                """);
                            var warmup = new ToastNotification(xml)
                            {
                                SuppressPopup = true,
                                Tag = "qnote-warmup",
                                Group = "warmup",
                                ExpirationTime = DateTimeOffset.Now.AddSeconds(15),
                            };
                            ToastNotificationManager.CreateToastNotifier(Aumid).Show(warmup);
                            ToastNotificationManager.History.Remove("qnote-warmup", "warmup", Aumid);
                            aumidKey.SetValue("HasSentNotification", 1);
                        }
                    }
                }
            }

            // In-process class object (both channels): warm clicks activate
            // the RUNNING process instead of spawning a new one.
            _factory = new ToastActivatorClassFactory();
            nint pUnk = Wrappers.GetOrCreateComInterfaceForObject(_factory, CreateComInterfaceFlags.None);
            try
            {
                int hr = Ole32Native.CoRegisterClassObject(ActivatorClsid, pUnk, ClsctxLocalServer, RegclsMultipleUse, out _);
                if (hr != 0)
                {
                    log.LogWarning(
                        "Toast activator CoRegisterClassObject failed (hr=0x{Hr:X8}); toast clicks degrade to plain launches this session (best-effort).",
                        hr);
                    _factory = null;
                    Volatile.Write(ref _registered, 0); // allow a retry on the next Register call
                }
                else
                {
                    log.LogInformation(
                        "Toast activator registered ({Channel} channel, CLSID {{{Clsid}}}).",
                        QNote.PackageIdentity.IsPackaged() ? "packaged" : "portable", ActivatorClsid);
                }
            }
            finally
            {
                Marshal.Release(pUnk); // COM holds its own reference once registered
            }
        }
        catch (Exception ex)
        {
            _factory = null;
            Volatile.Write(ref _registered, 0);
            log.LogWarning(ex, "Toast activator registration failed (best-effort; scheduling unaffected, clicks degrade to plain launches).");
        }
    }

    /// <summary>
    /// OS toast notifier for scheduling (both channels). Packaged: the
    /// parameterless overload resolves the package-identity AUMID.
    /// Unpackaged: the registered <see cref="Aumid"/>. Throws on WinRT
    /// failure — callers apply the best-effort contract.
    /// </summary>
    public static ToastNotifier CreateToastNotifier() =>
        QNote.PackageIdentity.IsPackaged()
            ? ToastNotificationManager.CreateToastNotifier()
            : ToastNotificationManager.CreateToastNotifier(Aumid);

    /// <summary>
    /// COM class factory serving <see cref="TodoNotificationActivator"/> —
    /// the same shape the Toolkit's battle-tested
    /// <c>NotificationActivatorClassFactory</c> used, but source-generated:
    /// <c>[GeneratedComClass]</c> emits the CCW vtable (IUnknown + factory
    /// interface) at compile time, so CreateInstance runs under NativeAOT.
    /// </summary>
    [GeneratedComClass]
    private sealed partial class ToastActivatorClassFactory : IToastActivatorClassFactory
    {
        public int CreateInstance(nint pUnkOuter, in Guid riid, out nint ppvObject)
        {
            ppvObject = 0;
            if (pUnkOuter != 0)
                return -2147221232; // CLASS_E_NOAGGREGATION

            // The notification platform's long-observed CoCreateInstance
            // quirk (mirrored from the Toolkit factory, both generations): it
            // requests the CLSID itself or IUnknown, not just the callback
            // IID — accept all three and always hand back the activator's
            // callback interface, like the Toolkit did.
            if (riid != ActivatorClsid && riid != CallbackIid && riid != IidIUnknown)
                return -2147467262; // E_NOINTERFACE

            nint pUnk = Wrappers.GetOrCreateComInterfaceForObject(
                new TodoNotificationActivator(), CreateComInterfaceFlags.None);
            try
            {
                return Marshal.QueryInterface(pUnk, CallbackIid, out ppvObject);
            }
            finally
            {
                Marshal.Release(pUnk);
            }
        }

        public int LockServer(bool fLock) => 0;
    }
}

/// <summary>
/// The OS toast-activation COM callback (INotificationActivationCallback,
/// shell contract 53E31837-…). <c>[GeneratedComInterface]</c> +
/// <c>[GeneratedComClass]</c> emit the interop (marshaling stubs, vtables,
/// QueryInterface) at compile time — the AOT-safe replacement for the
/// ComImport + Reflection.Emit pair the Toolkit Compat used. The data
/// array (toast textbox inputs) is kept as a raw pointer: todo reminder
/// toasts carry no inputs — everything rides in <c>invokedArgs</c>.
/// </summary>
[GeneratedComInterface]
[Guid("53E31837-6600-4A81-9395-75CFFE746F94")]
internal unsafe partial interface INotificationActivationCallback
{
    void Activate(
        [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
        [MarshalAs(UnmanagedType.LPWStr)] string invokedArgs,
        nint data,
        uint dataCount);
}

/// <summary>
/// Concrete activator: one instance per toast click, created on the COM
/// thread the OS activates on. Decodes the payload and hands it to the same
/// warm/cold delivery path the Compat version used (marshal to the UI thread
/// or stash until the window exists).
/// </summary>
[GeneratedComClass]
internal sealed partial class TodoNotificationActivator : INotificationActivationCallback
{
    public void Activate(string appUserModelId, string invokedArgs, nint data, uint dataCount) =>
        TodoToastActivation.HandleToastActivation(invokedArgs);
}

/// <summary>Classic COM IClassFactory, source-generated (AOT-safe vtable).</summary>
[GeneratedComInterface]
[Guid("00000001-0000-0000-C000-000000000046")]
internal unsafe partial interface IToastActivatorClassFactory
{
    [PreserveSig]
    int CreateInstance(nint pUnkOuter, in Guid riid, out nint ppvObject);

    [PreserveSig]
    int LockServer([MarshalAs(UnmanagedType.Bool)] bool fLock);
}

/// <summary>P/Invoke for COM registration with AOT-clean signatures (nint, no COM marshaling — avoids IL2050).</summary>
internal static partial class Ole32Native
{
    [DllImport("ole32.dll")]
    internal static extern int CoRegisterClassObject(in Guid rclsid, nint pUnk, uint dwClsContext, uint flags, out uint lpdwRegister);
}
