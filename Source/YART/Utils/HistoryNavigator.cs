using System.Collections.Generic;
using Verse;

namespace YART.Utils
{
    /// <summary>
    /// 웹 브라우저 스타일의 뒤로/앞으로 가기 네비게이션 기능을 제공합니다.
    /// </summary>
    /// <typeparam name="T">저장할 타입</typeparam>
    public class HistoryNavigator<T>
    {
        private readonly List<T> _history = new List<T>();
        private int _curIdx = -1;

        public HistoryNavigator() { }

        public bool CanUndo => _curIdx > 0;
        public bool CanRedo => _curIdx < _history.Count - 1;
        public bool HasCurrent => _curIdx >= 0 && _curIdx < _history.Count;

        public T Current
        {
            get
            {
                if (!HasCurrent) throw new System.InvalidOperationException("No current item");
                return _history[_curIdx];
            }
        }

        public void Push(T item)
        {
            // 현재 위치 이후의 기록은 모두 삭제
            if (_curIdx < _history.Count - 1)
            {
                _history.RemoveRange(_curIdx + 1, _history.Count - (_curIdx + 1));
            }
            _history.Add(item);
            _curIdx = _history.Count - 1;
        }

        public void Undo()
        {
            if (!CanUndo) throw new System.InvalidOperationException("Cannot undo");
            _curIdx--;
        }

        public void Redo()
        {
            if (!CanRedo) throw new System.InvalidOperationException("Cannot redo");
            _curIdx++;
        }

        public void Clear()
        {
            _history.Clear();
            _curIdx = -1;
        }
    }
}
