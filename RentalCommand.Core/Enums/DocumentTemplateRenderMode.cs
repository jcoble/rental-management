namespace RentalCommand.Core.Enums;

/// <summary>How a template is rendered into a final PDF.</summary>
public enum DocumentTemplateRenderMode
{
    /// <summary>Preserve the landlord's original PDF and stamp values/signatures on top.</summary>
    Overlay = 1,

    /// <summary>Generate a new styled PDF from drafted/editable content.</summary>
    Restyle = 2,
}

