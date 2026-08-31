using System;

namespace WASPer_3DP.Painting
{
    internal static class WasperPaintSession
    {
        private static WeakReference<object> _activeOwner;
        private static Action _stopActive;

        internal static void Activate(object owner, Action stopActive)
        {
            if (owner == null)
                return;
            if (_activeOwner != null &&
                _activeOwner.TryGetTarget(out object previous) &&
                previous != null &&
                !ReferenceEquals(previous, owner))
                _stopActive?.Invoke();
            _activeOwner = new WeakReference<object>(owner);
            _stopActive = stopActive;
        }

        internal static void Release(object owner)
        {
            if (_activeOwner != null &&
                _activeOwner.TryGetTarget(out object current) &&
                ReferenceEquals(current, owner))
            {
                _activeOwner = null;
                _stopActive = null;
            }
        }
    }
}
