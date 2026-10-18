namespace Cotabby.Core.Overlay;

/// <summary>Optional overlay capability for applying a resolved presentation mode.</summary>
public interface IOverlayDisplayMode
{
    void SetDisplayMode(string displayMode);
}
