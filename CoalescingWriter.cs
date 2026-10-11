using System;
using System.Threading;

namespace DarkVolumeMixer
{
    /// <summary>
    /// Nimmt Werte entgegen und schreibt immer nur den NEUESTEN – streng nacheinander, nie parallel.
    /// Ersetzt die vielen einzelnen ThreadPool-Aufträge pro Slider-Bewegung. Die alte Variante hatte
    /// zwei Probleme: Sie belastete den Pool unnötig, und die Aufträge liefen in beliebiger Reihenfolge,
    /// sodass ein ALTER Wert nach einem neuen geschrieben werden konnte (Ursache für das Hin-und-her-Springen).
    /// </summary>
    internal sealed class CoalescingWriter<T> where T : struct
    {
        private readonly Action<T> _write;
        private readonly object _gate = new();
        private T _pending;
        private bool _hasPending;
        private bool _running;

        public CoalescingWriter(Action<T> write) => _write = write;

        /// <summary>True, solange noch ein Wert geschrieben wird oder auf das Schreiben wartet.</summary>
        public bool IsBusy
        {
            get { lock (_gate) { return _running || _hasPending; } }
        }

        public void Post(T value)
        {
            lock (_gate)
            {
                _pending = value;
                _hasPending = true;
                if (_running) return;   // der laufende Worker holt sich den neuesten Wert selbst
                _running = true;
            }
            ThreadPool.QueueUserWorkItem(_ => Drain());
        }

        private void Drain()
        {
            while (true)
            {
                T value;
                lock (_gate)
                {
                    if (!_hasPending)
                    {
                        _running = false;
                        return;
                    }
                    value = _pending;
                    _hasPending = false;
                }

                try { _write(value); } catch { }
            }
        }
    }
}
