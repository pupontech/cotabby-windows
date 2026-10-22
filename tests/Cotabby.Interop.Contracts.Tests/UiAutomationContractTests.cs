using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using Cotabby.Win32.Interop;

namespace Cotabby.Interop.Contracts.Tests;

public sealed class UiAutomationContractTests
{
    [Fact]
    public void Automation2MethodsMatchTheWindowsSdkSlotOrderAndInterfaceIdentity()
    {
        Assert.Equal("34723aff-0c9d-49d0-9896-7ab52df8cd8a", typeof(IUIAutomation2).GUID.ToString());
        Assert.Equal(
            new[]
            {
                "get_AutoSetFocus",
                "put_AutoSetFocus",
                "get_ConnectionTimeout",
                "put_ConnectionTimeout",
                "get_TransactionTimeout",
                "put_TransactionTimeout",
            },
            DeclaredMethodNames(typeof(IUIAutomation2)));
    }

    [Fact]
    public void AutomationElementMethodsMatchTheWindowsSdkSlotOrderAndInterfaceIdentity()
    {
        Assert.Equal("d22108aa-8ac5-49a5-837b-37bbb3d7591e", typeof(IUIAutomationElement).GUID.ToString());
        Assert.Equal(
            new[]
            {
                "SetFocus",
                "GetRuntimeId",
                "FindFirst",
                "FindAll",
                "FindFirstBuildCache",
                "FindAllBuildCache",
                "BuildUpdatedCache",
                "GetCurrentPropertyValue",
                "GetCurrentPropertyValueEx",
                "GetCachedPropertyValue",
                "GetCachedPropertyValueEx",
                "GetCurrentPatternAs",
                "GetCachedPatternAs",
                "GetCurrentPattern",
                "GetCachedPattern",
            },
            DeclaredMethodNames(typeof(IUIAutomationElement)));
    }

    [Fact]
    public void TextPatternMethodsMatchTheWindowsSdkSlotOrderAndInterfaceIdentity()
    {
        Assert.Equal("32eba289-3583-42c9-9c59-3b6d9a1e9b6a", typeof(IUIAutomationTextPattern).GUID.ToString());
        Assert.Equal(
            new[]
            {
                "RangeFromPoint",
                "RangeFromChild",
                "GetSelection",
                "GetVisibleRanges",
                "get_DocumentRange",
                "get_SupportedTextSelection",
            },
            DeclaredMethodNames(typeof(IUIAutomationTextPattern)));
    }

    [Fact]
    public void AutomationBaseAndTextRangeRetainTheirWindowsSdkInterfaceIdentities()
    {
        Assert.Equal("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee", typeof(IUIAutomation).GUID.ToString());
        Assert.Equal("e22ad333-b25f-460c-83d0-0581107395c9", typeof(CUIAutomationClass).GUID.ToString());
        Assert.Equal("a543cc6a-f4ae-494b-8239-c814481187a8", typeof(IUIAutomationTextRange).GUID.ToString());
    }

    [Fact]
    public void AutomationBaseFocusMethodRetainsItsWindowsSdkSlotAndOutInterfaceContract()
    {
        var methods = DeclaredMethods(typeof(IUIAutomation));

        Assert.Equal(55, methods.Length);
        var getFocusedElement = methods[5];
        Assert.Equal("GetFocusedElement", getFocusedElement.Name);
        AssertPreserveSigInt(getFocusedElement);
        AssertParameter(getFocusedElement, 0, typeof(IUIAutomationElement).MakeByRefType(), isOut: true,
            UnmanagedType.Interface);
    }

    [Fact]
    public void CalledAutomationMethodsRetainTheirSdkSignaturesAndMarshalling()
    {
        AssertTimeoutGetter("get_ConnectionTimeout");
        AssertTimeoutSetter("put_ConnectionTimeout");
        AssertTimeoutGetter("get_TransactionTimeout");
        AssertTimeoutSetter("put_TransactionTimeout");

        var getRuntimeId = DeclaredMethod(typeof(IUIAutomationElement), "GetRuntimeId");
        AssertPreserveSigInt(getRuntimeId);
        AssertParameter(getRuntimeId, 0, typeof(int[]).MakeByRefType(), isOut: true,
            UnmanagedType.SafeArray, VarEnum.VT_I4);

        var getCurrentPropertyValue = DeclaredMethod(typeof(IUIAutomationElement), "GetCurrentPropertyValue");
        AssertPreserveSigInt(getCurrentPropertyValue);
        AssertParameter(getCurrentPropertyValue, 0, typeof(int), isOut: false, marshalAs: null);
        AssertParameter(getCurrentPropertyValue, 1, typeof(object).MakeByRefType(), isOut: true,
            UnmanagedType.Struct);

        var getCurrentPattern = DeclaredMethod(typeof(IUIAutomationElement), "GetCurrentPattern");
        AssertPreserveSigInt(getCurrentPattern);
        AssertParameter(getCurrentPattern, 0, typeof(int), isOut: false, marshalAs: null);
        AssertParameter(getCurrentPattern, 1, typeof(object).MakeByRefType(), isOut: true,
            UnmanagedType.Interface);
    }

    [Fact]
    public void CalledTextRangeMethodsRetainTheirSdkOrderSignaturesAndPreserveSig()
    {
        var textRange = typeof(IUIAutomationTextRange);
        var methods = DeclaredMethodNames(textRange);
        Assert.Equal(
            new[] { "Clone", "CompareEndpoints", "GetText", "MoveEndpointByRange" },
            new[] { methods[0], methods[2], methods[9], methods[12] });

        var clone = DeclaredMethod(textRange, "Clone");
        AssertPreserveSigInt(clone);
        AssertParameter(clone, 0, typeof(IUIAutomationTextRange).MakeByRefType(), isOut: true,
            UnmanagedType.Interface);

        var compareEndpoints = DeclaredMethod(textRange, "CompareEndpoints");
        AssertPreserveSigInt(compareEndpoints);
        AssertParameter(compareEndpoints, 0, typeof(int), isOut: false, marshalAs: null);
        AssertParameter(compareEndpoints, 1, typeof(IUIAutomationTextRange), isOut: false,
            UnmanagedType.Interface);
        AssertParameter(compareEndpoints, 2, typeof(int), isOut: false, marshalAs: null);
        AssertParameter(compareEndpoints, 3, typeof(int).MakeByRefType(), isOut: true, marshalAs: null);

        var getText = DeclaredMethod(textRange, "GetText");
        AssertPreserveSigInt(getText);
        AssertParameter(getText, 0, typeof(int), isOut: false, marshalAs: null);
        AssertParameter(getText, 1, typeof(string).MakeByRefType(), isOut: true, UnmanagedType.BStr);

        var moveEndpointByRange = DeclaredMethod(textRange, "MoveEndpointByRange");
        AssertPreserveSigInt(moveEndpointByRange);
        AssertParameter(moveEndpointByRange, 0, typeof(int), isOut: false, marshalAs: null);
        AssertParameter(moveEndpointByRange, 1, typeof(IUIAutomationTextRange), isOut: false,
            UnmanagedType.Interface);
        AssertParameter(moveEndpointByRange, 2, typeof(int), isOut: false, marshalAs: null);
    }

    [Fact]
    public void TextPatternSelectionReturnsTheSdkArrayInterfaceNotASafeArray()
    {
        Type? rangeArrayType = typeof(IUIAutomationTextPattern).Assembly
            .GetType("Cotabby.Win32.Interop.IUIAutomationTextRangeArray");

        Assert.NotNull(rangeArrayType);
        Assert.Equal("ce4ae76a-e717-4c98-81ea-47371d028eb6", rangeArrayType!.GUID.ToString());
        Assert.Equal(new[] { "get_Length", "GetElement" }, DeclaredMethodNames(rangeArrayType));

        var getLength = rangeArrayType.GetMethod("get_Length")!;
        var lengthParameter = Assert.Single(getLength.GetParameters());
        Assert.True(lengthParameter.IsOut);
        Assert.Equal(typeof(int).MakeByRefType(), lengthParameter.ParameterType);
        var getElement = rangeArrayType.GetMethod("GetElement")!;
        Assert.Equal(typeof(int), getElement.GetParameters()[0].ParameterType);
        var elementParameter = getElement.GetParameters()[1];
        Assert.True(elementParameter.IsOut);
        Assert.Equal(typeof(IUIAutomationTextRange), elementParameter.ParameterType.GetElementType());
        Assert.Equal(UnmanagedType.Interface, elementParameter.GetCustomAttribute<MarshalAsAttribute>()?.Value);

        var getSelection = typeof(IUIAutomationTextPattern).GetMethod("GetSelection")!;
        var parameter = Assert.Single(getSelection.GetParameters());
        Assert.True(parameter.ParameterType.IsByRef);
        Assert.Equal(rangeArrayType, parameter.ParameterType.GetElementType());
        Assert.True(parameter.IsOut);
        Assert.Equal(UnmanagedType.Interface, parameter.GetCustomAttribute<MarshalAsAttribute>()?.Value);
    }

    private static string[] DeclaredMethodNames(Type interfaceType) => DeclaredMethods(interfaceType)
        .Select(method => method.Name)
        .ToArray();

    private static MethodInfo[] DeclaredMethods(Type interfaceType) => interfaceType
        .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
        .OrderBy(method => method.MetadataToken)
        .ToArray();

    private static MethodInfo DeclaredMethod(Type interfaceType, string methodName) =>
        Assert.Single(DeclaredMethods(interfaceType), method => method.Name == methodName);

    private static void AssertPreserveSigInt(MethodInfo method)
    {
        Assert.Equal(typeof(int), method.ReturnType);
        Assert.NotNull(method.GetCustomAttribute<PreserveSigAttribute>());
    }

    private static void AssertTimeoutGetter(string methodName)
    {
        var method = DeclaredMethod(typeof(IUIAutomation2), methodName);
        AssertPreserveSigInt(method);
        AssertParameter(method, 0, typeof(uint).MakeByRefType(), isOut: true, marshalAs: null);
    }

    private static void AssertTimeoutSetter(string methodName)
    {
        var method = DeclaredMethod(typeof(IUIAutomation2), methodName);
        AssertPreserveSigInt(method);
        AssertParameter(method, 0, typeof(uint), isOut: false, marshalAs: null);
    }

    private static void AssertParameter(
        MethodInfo method,
        int index,
        Type expectedType,
        bool isOut,
        UnmanagedType? marshalAs,
        VarEnum? safeArraySubType = null)
    {
        var parameter = method.GetParameters()[index];
        Assert.Equal(expectedType, parameter.ParameterType);
        Assert.Equal(isOut, parameter.IsOut);
        var marshalAttribute = parameter.GetCustomAttribute<MarshalAsAttribute>();
        Assert.Equal(marshalAs, marshalAttribute?.Value);
        if (safeArraySubType is not null)
        {
            Assert.Equal(
                new[] { (byte)UnmanagedType.SafeArray, (byte)safeArraySubType.Value },
                GetMarshallingDescriptor(method, index));
        }
    }

    private static byte[] GetMarshallingDescriptor(MethodInfo method, int parameterIndex)
    {
        using var stream = File.OpenRead(method.Module.Assembly.Location);
        using var peReader = new PEReader(stream);
        MetadataReader metadata = peReader.GetMetadataReader();
        var typeHandle = metadata.TypeDefinitions.Single(handle =>
        {
            var definition = metadata.GetTypeDefinition(handle);
            return metadata.GetString(definition.Namespace) == method.DeclaringType!.Namespace
                && metadata.GetString(definition.Name) == method.DeclaringType.Name;
        });
        var methodHandle = metadata.GetTypeDefinition(typeHandle).GetMethods().Single(handle =>
            metadata.GetString(metadata.GetMethodDefinition(handle).Name) == method.Name);
        var parameterHandle = metadata.GetMethodDefinition(methodHandle).GetParameters().Single(handle =>
            metadata.GetParameter(handle).SequenceNumber == parameterIndex + 1);
        var descriptor = metadata.GetParameter(parameterHandle).GetMarshallingDescriptor();
        return metadata.GetBlobBytes(descriptor);
    }
}
