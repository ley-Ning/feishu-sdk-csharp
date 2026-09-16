namespace Feishu.EventBus;

/// <summary>IObservable 便捷订阅扩展（无 Rx 依赖时的 Action 形态订阅）。</summary>
public static class ObservableExtensions
{
    /// <summary>用 Action 订阅可观察流（等价 Rx 的 Subscribe(onNext)）。</summary>
    public static IDisposable Subscribe<T>(this IObservable<T> source, Action<T> onNext) =>
        source.Subscribe(new ActionObserver<T>(onNext));

    private sealed class ActionObserver<T>(Action<T> onNext) : IObserver<T>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(T value) => onNext(value);
    }
}
