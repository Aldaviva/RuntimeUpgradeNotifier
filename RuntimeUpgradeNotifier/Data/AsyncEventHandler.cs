namespace RuntimeUpgrade.Notifier.Data;

/// <summary>
/// An event whose callback can be an asynchronous method, so if your callback calls asynchronous code you can avoid putting it all in a gross <see cref="Task.Run(System.Action)"/> hack.
/// </summary>
/// <typeparam name="T">The type of the event arguments passed to the callback</typeparam>
public delegate ValueTask AsyncEventHandler<in T>(object? sender, T eventArgs);