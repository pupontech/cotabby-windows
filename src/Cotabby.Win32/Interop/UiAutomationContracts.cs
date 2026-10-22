using System.Runtime.InteropServices;

namespace Cotabby.Win32.Interop;

[ComImport, Guid("e22ad333-b25f-460c-83d0-0581107395c9"), ClassInterface(ClassInterfaceType.None)]
internal sealed class CUIAutomationClass { }

[ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomation
{
    [PreserveSig] int CompareElements();
    [PreserveSig] int CompareRuntimeIds();
    [PreserveSig] int GetRootElement();
    [PreserveSig] int ElementFromHandle();
    [PreserveSig] int ElementFromPoint();
    [PreserveSig] int GetFocusedElement([MarshalAs(UnmanagedType.Interface)] out IUIAutomationElement element);
    [PreserveSig] int GetRootElementBuildCache();
    [PreserveSig] int ElementFromHandleBuildCache();
    [PreserveSig] int ElementFromPointBuildCache();
    [PreserveSig] int GetFocusedElementBuildCache();
    [PreserveSig] int CreateTreeWalker();
    [PreserveSig] int get_ControlViewWalker();
    [PreserveSig] int get_ContentViewWalker();
    [PreserveSig] int get_RawViewWalker();
    [PreserveSig] int get_RawViewCondition();
    [PreserveSig] int get_ControlViewCondition();
    [PreserveSig] int get_ContentViewCondition();
    [PreserveSig] int CreateCacheRequest();
    [PreserveSig] int CreateTrueCondition();
    [PreserveSig] int CreateFalseCondition();
    [PreserveSig] int CreatePropertyCondition();
    [PreserveSig] int CreatePropertyConditionEx();
    [PreserveSig] int CreateAndCondition();
    [PreserveSig] int CreateAndConditionFromArray();
    [PreserveSig] int CreateAndConditionFromNativeArray();
    [PreserveSig] int CreateOrCondition();
    [PreserveSig] int CreateOrConditionFromArray();
    [PreserveSig] int CreateOrConditionFromNativeArray();
    [PreserveSig] int CreateNotCondition();
    [PreserveSig] int AddAutomationEventHandler();
    [PreserveSig] int RemoveAutomationEventHandler();
    [PreserveSig] int AddPropertyChangedEventHandlerNativeArray();
    [PreserveSig] int AddPropertyChangedEventHandler();
    [PreserveSig] int RemovePropertyChangedEventHandler();
    [PreserveSig] int AddStructureChangedEventHandler();
    [PreserveSig] int RemoveStructureChangedEventHandler();
    [PreserveSig] int AddFocusChangedEventHandler();
    [PreserveSig] int RemoveFocusChangedEventHandler();
    [PreserveSig] int RemoveAllEventHandlers();
    [PreserveSig] int IntNativeArrayToSafeArray();
    [PreserveSig] int IntSafeArrayToNativeArray();
    [PreserveSig] int RectToVariant();
    [PreserveSig] int VariantToRect();
    [PreserveSig] int SafeArrayToRectNativeArray();
    [PreserveSig] int CreateProxyFactoryEntry();
    [PreserveSig] int get_ProxyFactoryMapping();
    [PreserveSig] int GetPropertyProgrammaticName();
    [PreserveSig] int GetPatternProgrammaticName();
    [PreserveSig] int PollForPotentialSupportedPatterns();
    [PreserveSig] int PollForPotentialSupportedProperties();
    [PreserveSig] int CheckNotSupported();
    [PreserveSig] int get_ReservedNotSupportedValue();
    [PreserveSig] int get_ReservedMixedAttributeValue();
    [PreserveSig] int ElementFromIAccessible();
    [PreserveSig] int ElementFromIAccessibleBuildCache();
}

[ComImport, Guid("34723aff-0c9d-49d0-9896-7ab52df8cd8a"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomation2 : IUIAutomation
{
    // ComImport builds this interface's vtable from its declared members only.
    // Redeclare every IUIAutomation slot before the IUIAutomation2 extensions.
    [PreserveSig] new int CompareElements();
    [PreserveSig] new int CompareRuntimeIds();
    [PreserveSig] new int GetRootElement();
    [PreserveSig] new int ElementFromHandle();
    [PreserveSig] new int ElementFromPoint();
    [PreserveSig] new int GetFocusedElement([MarshalAs(UnmanagedType.Interface)] out IUIAutomationElement element);
    [PreserveSig] new int GetRootElementBuildCache();
    [PreserveSig] new int ElementFromHandleBuildCache();
    [PreserveSig] new int ElementFromPointBuildCache();
    [PreserveSig] new int GetFocusedElementBuildCache();
    [PreserveSig] new int CreateTreeWalker();
    [PreserveSig] new int get_ControlViewWalker();
    [PreserveSig] new int get_ContentViewWalker();
    [PreserveSig] new int get_RawViewWalker();
    [PreserveSig] new int get_RawViewCondition();
    [PreserveSig] new int get_ControlViewCondition();
    [PreserveSig] new int get_ContentViewCondition();
    [PreserveSig] new int CreateCacheRequest();
    [PreserveSig] new int CreateTrueCondition();
    [PreserveSig] new int CreateFalseCondition();
    [PreserveSig] new int CreatePropertyCondition();
    [PreserveSig] new int CreatePropertyConditionEx();
    [PreserveSig] new int CreateAndCondition();
    [PreserveSig] new int CreateAndConditionFromArray();
    [PreserveSig] new int CreateAndConditionFromNativeArray();
    [PreserveSig] new int CreateOrCondition();
    [PreserveSig] new int CreateOrConditionFromArray();
    [PreserveSig] new int CreateOrConditionFromNativeArray();
    [PreserveSig] new int CreateNotCondition();
    [PreserveSig] new int AddAutomationEventHandler();
    [PreserveSig] new int RemoveAutomationEventHandler();
    [PreserveSig] new int AddPropertyChangedEventHandlerNativeArray();
    [PreserveSig] new int AddPropertyChangedEventHandler();
    [PreserveSig] new int RemovePropertyChangedEventHandler();
    [PreserveSig] new int AddStructureChangedEventHandler();
    [PreserveSig] new int RemoveStructureChangedEventHandler();
    [PreserveSig] new int AddFocusChangedEventHandler();
    [PreserveSig] new int RemoveFocusChangedEventHandler();
    [PreserveSig] new int RemoveAllEventHandlers();
    [PreserveSig] new int IntNativeArrayToSafeArray();
    [PreserveSig] new int IntSafeArrayToNativeArray();
    [PreserveSig] new int RectToVariant();
    [PreserveSig] new int VariantToRect();
    [PreserveSig] new int SafeArrayToRectNativeArray();
    [PreserveSig] new int CreateProxyFactoryEntry();
    [PreserveSig] new int get_ProxyFactoryMapping();
    [PreserveSig] new int GetPropertyProgrammaticName();
    [PreserveSig] new int GetPatternProgrammaticName();
    [PreserveSig] new int PollForPotentialSupportedPatterns();
    [PreserveSig] new int PollForPotentialSupportedProperties();
    [PreserveSig] new int CheckNotSupported();
    [PreserveSig] new int get_ReservedNotSupportedValue();
    [PreserveSig] new int get_ReservedMixedAttributeValue();
    [PreserveSig] new int ElementFromIAccessible();
    [PreserveSig] new int ElementFromIAccessibleBuildCache();

    [PreserveSig] int get_AutoSetFocus([MarshalAs(UnmanagedType.Bool)] out bool value);
    [PreserveSig] int put_AutoSetFocus([MarshalAs(UnmanagedType.Bool)] bool value);
    [PreserveSig] int get_ConnectionTimeout(out uint value);
    [PreserveSig] int put_ConnectionTimeout(uint value);
    [PreserveSig] int get_TransactionTimeout(out uint value);
    [PreserveSig] int put_TransactionTimeout(uint value);
}

[ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationElement
{
    [PreserveSig] int SetFocus();
    [PreserveSig] int GetRuntimeId([MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_I4)] out int[] runtimeId);
    [PreserveSig] int FindFirst(int scope, IntPtr condition, [MarshalAs(UnmanagedType.Interface)] out IUIAutomationElement found);
    [PreserveSig] int FindAll(int scope, IntPtr condition, out IntPtr found);
    [PreserveSig] int FindFirstBuildCache(int scope, IntPtr condition, IntPtr cacheRequest, [MarshalAs(UnmanagedType.Interface)] out IUIAutomationElement found);
    [PreserveSig] int FindAllBuildCache(int scope, IntPtr condition, IntPtr cacheRequest, out IntPtr found);
    [PreserveSig] int BuildUpdatedCache(IntPtr cacheRequest, [MarshalAs(UnmanagedType.Interface)] out IUIAutomationElement updatedElement);
    [PreserveSig] int GetCurrentPropertyValue(int propertyId, [MarshalAs(UnmanagedType.Struct)] out object value);
    [PreserveSig] int GetCurrentPropertyValueEx(int propertyId, [MarshalAs(UnmanagedType.Bool)] bool ignoreDefaultValue, [MarshalAs(UnmanagedType.Struct)] out object value);
    [PreserveSig] int GetCachedPropertyValue(int propertyId, [MarshalAs(UnmanagedType.Struct)] out object value);
    [PreserveSig] int GetCachedPropertyValueEx(int propertyId, [MarshalAs(UnmanagedType.Bool)] bool ignoreDefaultValue, [MarshalAs(UnmanagedType.Struct)] out object value);
    [PreserveSig] int GetCurrentPatternAs(int patternId, in Guid riid, out IntPtr pattern);
    [PreserveSig] int GetCachedPatternAs(int patternId, in Guid riid, out IntPtr pattern);
    [PreserveSig] int GetCurrentPattern(int patternId, [MarshalAs(UnmanagedType.Interface)] out object pattern);
    [PreserveSig] int GetCachedPattern(int patternId, [MarshalAs(UnmanagedType.Interface)] out object pattern);
}

[ComImport, Guid("32eba289-3583-42c9-9c59-3b6d9a1e9b6a"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationTextPattern
{
    [PreserveSig] int RangeFromPoint(System.Drawing.Point point, [MarshalAs(UnmanagedType.Interface)] out IUIAutomationTextRange range);
    [PreserveSig] int RangeFromChild([MarshalAs(UnmanagedType.Interface)] IUIAutomationElement child, [MarshalAs(UnmanagedType.Interface)] out IUIAutomationTextRange range);
    [PreserveSig] int GetSelection([MarshalAs(UnmanagedType.Interface)] out IUIAutomationTextRangeArray ranges);
    [PreserveSig] int GetVisibleRanges([MarshalAs(UnmanagedType.Interface)] out IUIAutomationTextRangeArray ranges);
    [PreserveSig] int get_DocumentRange([MarshalAs(UnmanagedType.Interface)] out IUIAutomationTextRange range);
    [PreserveSig] int get_SupportedTextSelection(out int supportedTextSelection);
}

[ComImport, Guid("ce4ae76a-e717-4c98-81ea-47371d028eb6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationTextRangeArray
{
    [PreserveSig] int get_Length(out int length);
    [PreserveSig] int GetElement(int index, [MarshalAs(UnmanagedType.Interface)] out IUIAutomationTextRange element);
}

[ComImport, Guid("a543cc6a-f4ae-494b-8239-c814481187a8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationTextRange
{
    [PreserveSig] int Clone([MarshalAs(UnmanagedType.Interface)] out IUIAutomationTextRange range);
    [PreserveSig] int Compare([MarshalAs(UnmanagedType.Interface)] IUIAutomationTextRange range, [MarshalAs(UnmanagedType.Bool)] out bool areSame);
    [PreserveSig] int CompareEndpoints(int endpoint, [MarshalAs(UnmanagedType.Interface)] IUIAutomationTextRange targetRange, int targetEndpoint, out int result);
    [PreserveSig] int ExpandToEnclosingUnit(int unit);
    [PreserveSig] int FindAttribute(int attributeId, [MarshalAs(UnmanagedType.Struct)] object value, [MarshalAs(UnmanagedType.Bool)] bool backward, [MarshalAs(UnmanagedType.Interface)] out IUIAutomationTextRange found);
    [PreserveSig] int FindText([MarshalAs(UnmanagedType.BStr)] string text, [MarshalAs(UnmanagedType.Bool)] bool backward, [MarshalAs(UnmanagedType.Bool)] bool ignoreCase, [MarshalAs(UnmanagedType.Interface)] out IUIAutomationTextRange found);
    [PreserveSig] int GetAttributeValue(int attributeId, [MarshalAs(UnmanagedType.Struct)] out object value);
    [PreserveSig] int GetBoundingRectangles([MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_R8)] out double[] rectangles);
    [PreserveSig] int GetEnclosingElement([MarshalAs(UnmanagedType.Interface)] out IUIAutomationElement element);
    [PreserveSig] int GetText(int maxLength, [MarshalAs(UnmanagedType.BStr)] out string text);
    [PreserveSig] int Move(int unit, int count, out int moved);
    [PreserveSig] int MoveEndpointByUnit(int endpoint, int unit, int count, out int moved);
    [PreserveSig] int MoveEndpointByRange(int endpoint, [MarshalAs(UnmanagedType.Interface)] IUIAutomationTextRange targetRange, int targetEndpoint);
    [PreserveSig] int Select();
    [PreserveSig] int AddToSelection();
    [PreserveSig] int RemoveFromSelection();
    [PreserveSig] int ScrollIntoView([MarshalAs(UnmanagedType.Bool)] bool alignToTop);
    [PreserveSig] int GetChildren(out IntPtr children);
}
