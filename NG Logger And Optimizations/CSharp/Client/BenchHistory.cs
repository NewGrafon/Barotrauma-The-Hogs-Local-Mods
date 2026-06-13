using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace NetEventLogger
{
    // ==========================================================================================
    //  A small ring-buffer history of benchmark results, with a "currently viewed" index. The menu
    //  shows the viewed entry and the prev/next buttons step through past runs. Two instances exist:
    //  one for CLIENT benchmarks (local) and one for SERVER benchmarks (synced to all privileged clients).
    //  Each entry carries a local-time stamp + the coloured report lines.
    //  RUS: Маленькая история результатов бенчмарка (кольцевой буфер) с индексом «сейчас просматривается».
    //  RUS: Меню показывает просматриваемую запись, кнопки назад/вперёд листают прошлые прогоны. Два экземпляра:
    //  RUS: для КЛИЕНТСКИХ бенчмарков (локально) и для СЕРВЕРНЫХ (синхронизируется всем привилегированным).
    //  RUS: Каждая запись несёт метку локального времени + цветные строки отчёта.
    // ==========================================================================================
    public sealed class BenchHistory
    {
        public sealed class Entry
        {
            public string Stamp;
            public List<(string Text, Color Color)> Lines;
        }

        private const int MaxEntries = 20;
        private readonly List<Entry> _entries = new List<Entry>();
        private int _view = -1;

        // Set whenever the visible content changes (new entry / navigation) so the menu refreshes.
        // RUS: Ставится при любом изменении видимого (новая запись / навигация), чтобы меню обновилось.
        public bool Dirty;

        public int Count => _entries.Count;
        public int ViewIndex => _view;

        public void Add(string stamp, List<(string Text, Color Color)> lines)
        {
            _entries.Add(new Entry { Stamp = stamp ?? "", Lines = lines ?? new List<(string Text, Color Color)>() });
            while (_entries.Count > MaxEntries) { _entries.RemoveAt(0); }
            _view = _entries.Count - 1; // jump to the newest   // RUS: прыгаем на самую свежую
            Dirty = true;
        }

        public Entry Current => (_view >= 0 && _view < _entries.Count) ? _entries[_view] : null;

        public void Prev() { if (_view > 0) { _view--; Dirty = true; } }
        public void Next() { if (_view < _entries.Count - 1) { _view++; Dirty = true; } }
    }
}
