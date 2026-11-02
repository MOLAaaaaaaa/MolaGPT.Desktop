using System.Runtime.InteropServices;

namespace MolaGPT.App.Selection;

// Windows SDK UIAutomationClient.h, IUnknown vtable order. Unused slots are declared
// only to preserve the ABI and must not be called. No WPF runtime dependency.
[ComImport, Guid("34723AFF-0C9D-49D0-9896-7AB52DF8CD8A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUiaAutomation
{
    void CompareElements();
    void CompareRuntimeIds();
    void GetRootElement();
    void ElementFromHandle(nint hwnd, out IUiaElement element);
    void ElementFromPoint(SelectionNative.Point point, out IUiaElement element);
    void GetFocusedElement(out IUiaElement element);
    void GetRootElementBuildCache();
    void ElementFromHandleBuildCache();
    void ElementFromPointBuildCache(SelectionNative.Point point, IUiaCacheRequest cache, out IUiaElement element);
    void GetFocusedElementBuildCache(IUiaCacheRequest cache, out IUiaElement element);
    void CreateTreeWalker();
    void get_ControlViewWalker();
    void get_ContentViewWalker();
    void get_RawViewWalker(out IUiaWalker walker);
    void get_RawViewCondition();
    void get_ControlViewCondition();
    void get_ContentViewCondition();
    void CreateCacheRequest(out IUiaCacheRequest cache);
    void CreateTrueCondition();
    void CreateFalseCondition();
    void CreatePropertyCondition();
    void CreatePropertyConditionEx();
    void CreateAndCondition();
    void CreateAndConditionFromArray();
    void CreateAndConditionFromNativeArray();
    void CreateOrCondition();
    void CreateOrConditionFromArray();
    void CreateOrConditionFromNativeArray();
    void CreateNotCondition();
    void AddAutomationEventHandler();
    void RemoveAutomationEventHandler();
    void AddPropertyChangedEventHandlerNativeArray();
    void AddPropertyChangedEventHandler();
    void RemovePropertyChangedEventHandler();
    void AddStructureChangedEventHandler();
    void RemoveStructureChangedEventHandler();
    void AddFocusChangedEventHandler();
    void RemoveFocusChangedEventHandler();
    void RemoveAllEventHandlers();
    void IntNativeArrayToSafeArray();
    void IntSafeArrayToNativeArray();
    void RectToVariant();
    void VariantToRect();
    void SafeArrayToRectNativeArray();
    void CreateProxyFactoryEntry();
    void get_ProxyFactoryMapping();
    void GetPropertyProgrammaticName();
    void GetPatternProgrammaticName();
    void PollForPotentialSupportedPatterns();
    void PollForPotentialSupportedProperties();
    void CheckNotSupported();
    void get_ReservedNotSupportedValue();
    void get_ReservedMixedAttributeValue();
    void ElementFromIAccessible();
    void ElementFromIAccessibleBuildCache();
    void get_AutoSetFocus();
    void put_AutoSetFocus([MarshalAs(UnmanagedType.Bool)] bool value);
    void get_ConnectionTimeout();
    void put_ConnectionTimeout(uint value);
    void get_TransactionTimeout();
    void put_TransactionTimeout(uint value);
}

[ComImport, Guid("D22108AA-8AC5-49A5-837B-37BBB3D7591E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUiaElement
{
    void SetFocus();
    void GetRuntimeId();
    void FindFirst();
    void FindAll();
    void FindFirstBuildCache();
    void FindAllBuildCache();
    void BuildUpdatedCache();
    void GetCurrentPropertyValue(int id, [MarshalAs(UnmanagedType.Struct)] out object value);
    void GetCurrentPropertyValueEx();
    void GetCachedPropertyValue(int id, [MarshalAs(UnmanagedType.Struct)] out object value);
    void GetCachedPropertyValueEx();
    void GetCurrentPatternAs();
    void GetCachedPatternAs();
    void GetCurrentPattern(int id, [MarshalAs(UnmanagedType.IUnknown)] out object? pattern);
    void GetCachedPattern(int id, [MarshalAs(UnmanagedType.IUnknown)] out object? pattern);
}

[ComImport, Guid("B32A92B5-BC25-4078-9C08-D7EE95C48E03"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUiaCacheRequest
{
    void AddProperty(int id);
    void AddPattern(int id);
}

[ComImport, Guid("4042C624-389C-4AFC-A630-9DF854A541FC"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUiaWalker
{
    void GetParentElement();
    void GetFirstChildElement();
    void GetLastChildElement();
    void GetNextSiblingElement();
    void GetPreviousSiblingElement();
    void NormalizeElement();
    void GetParentElementBuildCache(IUiaElement element, IUiaCacheRequest cache, out IUiaElement? parent);
}

[ComImport, Guid("32EBA289-3583-42C9-9C59-3B6D9A1E9B6A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUiaTextPattern
{
    void RangeFromPoint();
    void RangeFromChild();
    void GetSelection(out IUiaRanges ranges);
}

[ComImport, Guid("CE4AE76A-E717-4C98-81EA-47371D028EB6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUiaRanges
{
    void get_Length(out int length);
    void GetElement(int index, out IUiaRange range);
}

[ComImport, Guid("A543CC6A-F4AE-494B-8239-C814481187A8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUiaRange
{
    void Clone();
    void Compare();
    void CompareEndpoints();
    void ExpandToEnclosingUnit();
    void FindAttribute();
    void FindText();
    void GetAttributeValue();
    void GetBoundingRectangles([MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_R8)] out double[] rectangles);
    void GetEnclosingElement();
    void GetText(int maxLength, [MarshalAs(UnmanagedType.BStr)] out string text);
}
