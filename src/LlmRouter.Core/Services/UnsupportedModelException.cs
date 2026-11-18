namespace LlmRouter.Core.Services;

/// <summary>
/// Thrown when no registered provider or logical alias supports the requested model.
/// Inherits <see cref="InvalidOperationException"/> for contract compatibility.
/// </summary>
public sealed class UnsupportedModelException : InvalidOperationException
{
    /// <summary>
    /// Creates an unsupported-model domain exception.
    /// </summary>
    public UnsupportedModelException(string model)
        : base($"No provider supports model {model}.")
    {
        Model = model;
    }

    /// <summary>Requested model or alias that could not be resolved.</summary>
    public string Model { get; }
}
