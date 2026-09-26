using System;
using System.Collections.Generic;

namespace CICUser.Views.SettingsPages;

/// <summary>
/// 多步撤回栈。用于设置页的「撤回」按钮，避免误触后无法还原。
/// </summary>
/// <remarks>
/// 设计要点：
/// <list type="bullet">
/// <item>保存的是操作前的完整快照，撤回时整份还原，避免部分字段回滚导致状态不一致</item>
/// <item>栈有容量上限，超出后丢弃最早的快照，避免长期使用占用过多内存</item>
/// <item>快照以字符串形式（JSON）保存，天然与对象实例解耦，不受后续修改影响</item>
/// </list>
/// </remarks>
/// <typeparam name="T">快照类型。</typeparam>
internal sealed class UndoStack<T>
{
    /// <summary>栈的容量上限。设置页操作频次低，20 步足够覆盖误触场景。</summary>
    private const int MaxDepth = 20;

    private readonly LinkedList<T> _stack = new();
    private readonly Func<T, T> _clone;
    private readonly Func<T, string> _serialize;

    /// <summary>
    /// 构造撤回栈。
    /// </summary>
    /// <param name="clone">快照的深拷贝函数，防止外部对象被后续修改影响。</param>
    /// <param name="serialize">把快照转为字符串用于比较，判断是否真的发生了变化。</param>
    public UndoStack(Func<T, T> clone, Func<T, string> serialize)
    {
        _clone = clone;
        _serialize = serialize;
    }

    /// <summary>当前栈内可撤回的步数。</summary>
    public int Count => _stack.Count;

    /// <summary>是否还有可撤回的内容。</summary>
    public bool CanUndo => _stack.Count > 0;

    /// <summary>
    /// 压入一份快照。
    /// </summary>
    /// <param name="state">操作前的状态。</param>
    public void Push(T state)
    {
        if (state == null)
        {
            return;
        }

        _stack.AddLast(_clone(state));

        // 超出容量上限时丢弃最早的快照
        while (_stack.Count > MaxDepth)
        {
            _stack.RemoveFirst();
        }
    }

    /// <summary>
    /// 与栈顶快照比较，若内容相同则不重复压栈。
    /// 用于在用户实际未改动任何内容时避免污染撤回栈。
    /// </summary>
    /// <param name="state">准备压入的状态。</param>
    /// <returns>本次是否真的压入了快照。</returns>
    public bool PushIfChanged(T state)
    {
        if (state == null)
        {
            return false;
        }

        if (_stack.Last != null &&
            _serialize(_stack.Last.Value) == _serialize(state))
        {
            return false;
        }

        Push(state);
        return true;
    }

    /// <summary>
    /// 弹出栈顶快照。栈空时返回 default。
    /// </summary>
    /// <returns>弹出快照的副本；无内容时返回 default。</returns>
    public T? Pop()
    {
        if (_stack.Last == null)
        {
            return default;
        }

        var value = _stack.Last.Value;
        _stack.RemoveLast();
        return _clone(value);
    }

    /// <summary>清空栈，通常在保存成功后调用。</summary>
    public void Clear() => _stack.Clear();
}
