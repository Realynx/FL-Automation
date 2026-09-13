namespace FruityLink.Core.Diagnostics;

/// <summary>A read-only snapshot of an exact-build FL message form, including verified native choices.</summary>
/// <param name="Window">Message form HWND.</param><param name="Body">Complete displayed message text.</param>
/// <param name="Choices">Choices whose native callback and modal result have been verified.</param>
public sealed record FlDialogInspection(nint Window, string Body, IReadOnlyList<FlDialogChoice> Choices);

/// <summary>A windowed FL button wrapper with a verified native modal result.</summary>
/// <param name="Window">Button wrapper HWND, suitable only for normal window input.</param>
/// <param name="Result">Native modal result: six means Yes, seven means No.</param>
public sealed record FlDialogChoice(nint Window, int Result);
