using System.Reflection;
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

    private static string[] DeclaredMethodNames(Type interfaceType) => interfaceType
        .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
        .OrderBy(method => method.MetadataToken)
        .Select(method => method.Name)
        .ToArray();
}
