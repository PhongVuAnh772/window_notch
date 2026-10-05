using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using WindowsNotch.Core;

namespace WindowsNotch.Services.DragDrop;

/// <summary>
/// Native Windows drag-and-drop service using OLE <c>IDropTarget</c> (<c>RegisterDragDrop</c> / <c>RevokeDragDrop</c>)
/// and <c>CF_HDROP</c> (<c>DragQueryFileW</c>) on the existing Notch HWND.
/// Validates normal filesystem files via Win32 <c>GetFileAttributesExW</c> without opening or reading file contents.
/// </summary>
public sealed class WindowsDragDropService : IDragDropService
{
    public const string FileTooLargeLabel = "File too large";

    private const int S_OK = 0;
    private const int S_FALSE = 1;
    private const short CF_HDROP = 15;
    private const uint DROPEFFECT_NONE = 0;
    private const uint DROPEFFECT_COPY = 1;

    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
    private const uint FILE_ATTRIBUTE_DEVICE = 0x00000040;
    private const int GetFileExInfoStandard = 0;
    private const int MaxPathBufferCapacity = 32768;

    private readonly object _stateGate = new();
    private readonly OleDropTargetBridge _dropTargetBridge;

    private nint _registeredHwnd;
    private bool _isOleInitialized;
    private bool _isDragDropRegistered;
    private bool _isDisposed;

    private Func<int, int, bool>? _hitTestInteractiveBounds;
    private Func<bool>? _canAcceptDrag;

    private DragDropState _currentState = DragDropState.Empty;
    private DragDropState _stateBeforeDrag = DragDropState.Empty;
    private bool _currentDragHasValidFile;
    private bool _isDraggingOverPill;

    public WindowsDragDropService()
    {
        _dropTargetBridge = new OleDropTargetBridge(this);
    }

    public DragDropState CurrentState
    {
        get
        {
            lock (_stateGate)
            {
                return _currentState;
            }
        }
    }

    public event EventHandler<DragDropState>? DragDropStateChanged;

    public void Start(nint windowHandle)
    {
        Start(windowHandle, hitTestInteractiveBounds: null, canAcceptDrag: null);
    }

    public void Start(
        nint windowHandle,
        Func<int, int, bool>? hitTestInteractiveBounds,
        Func<bool>? canAcceptDrag)
    {
        if (_isDisposed || windowHandle == nint.Zero)
        {
            return;
        }

        _hitTestInteractiveBounds = hitTestInteractiveBounds;
        _canAcceptDrag = canAcceptDrag;

        if (_isDragDropRegistered && _registeredHwnd == windowHandle)
        {
            return;
        }

        Stop();

        try
        {
            int oleHr = OleInitialize(nint.Zero);
            if (oleHr is S_OK or S_FALSE)
            {
                _isOleInitialized = true;
            }

            int registerHr = RegisterDragDrop(windowHandle, _dropTargetBridge);
            if (registerHr >= 0)
            {
                _registeredHwnd = windowHandle;
                _isDragDropRegistered = true;
            }
        }
        catch
        {
            // Fail gracefully without crashing if OLE registration is unavailable.
        }
    }

    public void Stop()
    {
        if (_isDragDropRegistered && _registeredHwnd != nint.Zero)
        {
            try
            {
                RevokeDragDrop(_registeredHwnd);
            }
            catch
            {
                // Ignore cleanup errors during window shutdown.
            }
        }

        _isDragDropRegistered = false;
        _registeredHwnd = nint.Zero;
        _currentDragHasValidFile = false;
        _isDraggingOverPill = false;

        if (_isOleInitialized)
        {
            try
            {
                OleUninitialize();
            }
            catch
            {
                // Ignore cleanup errors during shutdown.
            }

            _isOleInitialized = false;
        }
    }

    public void Clear()
    {
        bool changed = false;
        lock (_stateGate)
        {
            if (_currentState != DragDropState.Empty)
            {
                _currentState = DragDropState.Empty;
                _stateBeforeDrag = DragDropState.Empty;
                changed = true;
            }
        }

        _currentDragHasValidFile = false;
        _isDraggingOverPill = false;

        if (changed && !_isDisposed)
        {
            DragDropStateChanged?.Invoke(this, DragDropState.Empty);
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        Stop();

        lock (_stateGate)
        {
            _currentState = DragDropState.Empty;
            _stateBeforeDrag = DragDropState.Empty;
        }
    }

    private int HandleOleDragEnter(
        IDataObject? dataObject,
        POINTL point,
        ref uint effect)
    {
        if (_isDisposed || !CanAcceptDrag())
        {
            _currentDragHasValidFile = false;
            _isDraggingOverPill = false;
            effect = DROPEFFECT_NONE;
            return S_OK;
        }

        _currentDragHasValidFile = dataObject is not null &&
                                   TryExtractFirstValidFileFromDataObject(dataObject, out _);

        if (_currentDragHasValidFile && IsPointInInteractiveBounds(point.X, point.Y))
        {
            effect = DROPEFFECT_COPY;
            EnterDraggingState();
        }
        else
        {
            effect = DROPEFFECT_NONE;
        }

        return S_OK;
    }

    private int HandleOleDragOver(POINTL point, ref uint effect)
    {
        if (_isDisposed || !CanAcceptDrag() || !_currentDragHasValidFile)
        {
            effect = DROPEFFECT_NONE;
            if (_isDraggingOverPill)
            {
                LeaveDraggingState();
            }

            return S_OK;
        }

        if (IsPointInInteractiveBounds(point.X, point.Y))
        {
            effect = DROPEFFECT_COPY;
            if (!_isDraggingOverPill)
            {
                EnterDraggingState();
            }
        }
        else
        {
            effect = DROPEFFECT_NONE;
            if (_isDraggingOverPill)
            {
                LeaveDraggingState();
            }
        }

        return S_OK;
    }

    private int HandleOleDragLeave()
    {
        _currentDragHasValidFile = false;
        if (_isDraggingOverPill)
        {
            LeaveDraggingState();
        }

        return S_OK;
    }

    private int HandleOleDrop(
        IDataObject? dataObject,
        POINTL point,
        ref uint effect)
    {
        bool wasDraggingOverPill = _isDraggingOverPill;
        _currentDragHasValidFile = false;
        _isDraggingOverPill = false;

        if (_isDisposed ||
            !CanAcceptDrag() ||
            dataObject is null ||
            (!wasDraggingOverPill && !IsPointInInteractiveBounds(point.X, point.Y)))
        {
            effect = DROPEFFECT_NONE;
            if (wasDraggingOverPill)
            {
                RestorePreDragState();
            }

            return S_OK;
        }

        if (!TryExtractFirstValidFileFromDataObject(dataObject, out DroppedFileMetadata metadata))
        {
            effect = DROPEFFECT_NONE;
            RestorePreDragState();
            return S_OK;
        }

        effect = DROPEFFECT_COPY;
        PublishDroppedFileMetadata(metadata);
        return S_OK;
    }

    private void EnterDraggingState()
    {
        if (_isDraggingOverPill)
        {
            return;
        }

        _isDraggingOverPill = true;

        DragDropState nextState;
        lock (_stateGate)
        {
            _stateBeforeDrag = _currentState;
            nextState = new DragDropState(
                IsDragging: true,
                HasDropPreview: _stateBeforeDrag.HasDropPreview,
                FileName: _stateBeforeDrag.FileName,
                Extension: _stateBeforeDrag.Extension,
                FilePath: null,
                FileSizeBytes: _stateBeforeDrag.FileSizeBytes,
                IsSupported: true);
            _currentState = nextState;
        }

        DragDropStateChanged?.Invoke(this, nextState);
    }

    private void LeaveDraggingState()
    {
        if (!_isDraggingOverPill)
        {
            return;
        }

        _isDraggingOverPill = false;
        RestorePreDragState();
    }

    private void RestorePreDragState()
    {
        DragDropState restoredState;
        lock (_stateGate)
        {
            restoredState = _stateBeforeDrag;
            _currentState = restoredState;
        }

        if (!_isDisposed)
        {
            DragDropStateChanged?.Invoke(this, restoredState);
        }
    }

    private void PublishDroppedFileMetadata(DroppedFileMetadata metadata)
    {
        bool isWithinSizeLimit = metadata.FileSizeBytes <= DesignTokens.DragDrop.MaxFileSizeBytes;

        string displayLabel = isWithinSizeLimit
            ? metadata.DisplayFileName
            : FileTooLargeLabel;

        // Retain only display name, extension, and file size in memory; never hold full directory paths or file bytes.
        DragDropState droppedState = new(
            IsDragging: false,
            HasDropPreview: true,
            FileName: displayLabel,
            Extension: metadata.Extension,
            FilePath: null,
            FileSizeBytes: metadata.FileSizeBytes,
            IsSupported: isWithinSizeLimit);

        lock (_stateGate)
        {
            _currentState = droppedState;
            _stateBeforeDrag = droppedState;
        }

        if (!_isDisposed)
        {
            DragDropStateChanged?.Invoke(this, droppedState);
        }
    }

    private bool CanAcceptDrag()
    {
        return _canAcceptDrag?.Invoke() ?? true;
    }

    private bool IsPointInInteractiveBounds(int screenX, int screenY)
    {
        return _hitTestInteractiveBounds?.Invoke(screenX, screenY) ?? true;
    }

    private static bool TryExtractFirstValidFileFromDataObject(
        IDataObject dataObject,
        out DroppedFileMetadata metadata)
    {
        metadata = default;

        FORMATETC format = new()
        {
            cfFormat = CF_HDROP,
            ptd = nint.Zero,
            dwAspect = DVASPECT.DVASPECT_CONTENT,
            lindex = -1,
            tymed = TYMED.TYMED_HGLOBAL
        };

        try
        {
            if (dataObject.QueryGetData(ref format) != S_OK)
            {
                return false;
            }

            dataObject.GetData(ref format, out STGMEDIUM medium);
            try
            {
                if (medium.tymed != TYMED.TYMED_HGLOBAL || medium.unionmember == nint.Zero)
                {
                    return false;
                }

                return TryExtractFirstValidFileFromHdrop(medium.unionmember, out metadata);
            }
            finally
            {
                ReleaseStgMedium(ref medium);
            }
        }
        catch
        {
            return false;
        }
    }

    private static bool TryExtractFirstValidFileFromHdrop(
        nint hDrop,
        out DroppedFileMetadata metadata)
    {
        metadata = default;

        uint count = DragQueryFileW(hDrop, 0xFFFFFFFF, null, 0);
        if (count == 0)
        {
            return false;
        }

        // Accept ONLY the first valid normal filesystem file; reject directories and virtual items.
        for (uint index = 0; index < count; index++)
        {
            uint charCount = DragQueryFileW(hDrop, index, null, 0);
            if (charCount == 0 || charCount >= MaxPathBufferCapacity)
            {
                continue;
            }

            StringBuilder pathBuilder = new((int)charCount + 1);
            uint copied = DragQueryFileW(hDrop, index, pathBuilder, charCount + 1);
            if (copied == 0)
            {
                continue;
            }

            string candidatePath = pathBuilder.ToString();
            if (TryInspectFilesystemFileMetadata(candidatePath, out metadata))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Inspects file attributes and size using Win32 <c>GetFileAttributesExW</c> without opening the file or reading contents.
    /// Rejects directories, devices, URLs, and non-existent virtual shell items.
    /// </summary>
    private static bool TryInspectFilesystemFileMetadata(
        string candidatePath,
        out DroppedFileMetadata metadata)
    {
        metadata = default;

        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            return false;
        }

        if (!GetFileAttributesExW(candidatePath, GetFileExInfoStandard, out WIN32_FILE_ATTRIBUTE_DATA attributeData))
        {
            return false;
        }

        // Reject directories and device nodes.
        if ((attributeData.dwFileAttributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_DEVICE)) != 0)
        {
            return false;
        }

        string rawFileName = Path.GetFileName(candidatePath);
        string normalizedName = DragDropState.NormalizeFileName(
            rawFileName,
            DesignTokens.DragDrop.MaxFileNameCharacters);

        if (string.IsNullOrEmpty(normalizedName))
        {
            return false;
        }

        string rawExtension = Path.GetExtension(rawFileName);
        string normalizedExtension = string.IsNullOrWhiteSpace(rawExtension)
            ? string.Empty
            : rawExtension.Trim().ToLowerInvariant();

        ulong rawSize = ((ulong)attributeData.nFileSizeHigh << 32) | attributeData.nFileSizeLow;
        long fileSizeBytes = rawSize > long.MaxValue ? long.MaxValue : (long)rawSize;

        metadata = new DroppedFileMetadata(
            DisplayFileName: normalizedName,
            Extension: normalizedExtension,
            FileSizeBytes: fileSizeBytes);

        return true;
    }

    private readonly record struct DroppedFileMetadata(
        string DisplayFileName,
        string Extension,
        long FileSizeBytes);

    [ComVisible(true)]
    private sealed class OleDropTargetBridge : IOleDropTarget
    {
        private readonly WindowsDragDropService _owner;

        public OleDropTargetBridge(WindowsDragDropService owner)
        {
            _owner = owner;
        }

        public int DragEnter(IDataObject? pDataObj, uint grfKeyState, POINTL pt, ref uint pdwEffect)
        {
            return _owner.HandleOleDragEnter(pDataObj, pt, ref pdwEffect);
        }

        public int DragOver(uint grfKeyState, POINTL pt, ref uint pdwEffect)
        {
            return _owner.HandleOleDragOver(pt, ref pdwEffect);
        }

        public int DragLeave()
        {
            return _owner.HandleOleDragLeave();
        }

        public int Drop(IDataObject? pDataObj, uint grfKeyState, POINTL pt, ref uint pdwEffect)
        {
            return _owner.HandleOleDrop(pDataObj, pt, ref pdwEffect);
        }
    }

    [ComImport]
    [Guid("00000122-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IOleDropTarget
    {
        [PreserveSig]
        int DragEnter(
            [In, MarshalAs(UnmanagedType.Interface)] IDataObject? pDataObj,
            [In] uint grfKeyState,
            [In] POINTL pt,
            [In, Out] ref uint pdwEffect);

        [PreserveSig]
        int DragOver(
            [In] uint grfKeyState,
            [In] POINTL pt,
            [In, Out] ref uint pdwEffect);

        [PreserveSig]
        int DragLeave();

        [PreserveSig]
        int Drop(
            [In, MarshalAs(UnmanagedType.Interface)] IDataObject? pDataObj,
            [In] uint grfKeyState,
            [In] POINTL pt,
            [In, Out] ref uint pdwEffect);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTL
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WIN32_FILE_ATTRIBUTE_DATA
    {
        public uint dwFileAttributes;
        public FILETIME ftCreationTime;
        public FILETIME ftLastAccessTime;
        public FILETIME ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
    }

    [DllImport("ole32.dll")]
    private static extern int OleInitialize(nint pvReserved);

    [DllImport("ole32.dll")]
    private static extern void OleUninitialize();

    [DllImport("ole32.dll")]
    private static extern int RegisterDragDrop(
        nint hwnd,
        [MarshalAs(UnmanagedType.Interface)] IOleDropTarget pDropTarget);

    [DllImport("ole32.dll")]
    private static extern int RevokeDragDrop(nint hwnd);

    [DllImport("ole32.dll")]
    private static extern void ReleaseStgMedium(ref STGMEDIUM pmedium);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFileW(
        nint hDrop,
        uint iFile,
        StringBuilder? lpszFile,
        uint cch);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileAttributesExW(
        string lpFileName,
        int fInfoLevelId,
        out WIN32_FILE_ATTRIBUTE_DATA lpFileInformation);
}
