namespace RuntimeUpgrade.Notifier.Data;

/// <summary>Like <see cref="EventHandler"/>, but allows the delegate to return a Task to represent an async event handling operation.</summary>
/// <param name="sender">The object that fired the event.</param>
/// <returns>A <see cref="Task"/> that can be awaited if handling the event is asynchronous.</returns>
public delegate Task AsyncEventHandler(object? sender);

/// <summary>Like <see cref="EventHandler{T}"/>, but allows the delegate to return a Task to represent an async event handling operation.</summary>
/// <typeparam name="T">The type of arguments passed in the event.</typeparam>
/// <param name="sender">The object that fired the event.</param>
/// <param name="args">Arguments passed with the event.</param>
/// <returns>A <see cref="Task"/> that can be awaited if handling the event is asynchronous.</returns>
public delegate Task AsyncEventHandler<in T>(object? sender, T args);