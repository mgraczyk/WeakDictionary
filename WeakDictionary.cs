using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace mglib.WeakDictionary 
{
   public class WeakDictionary<T> where T : class
   {
      // Minimum number of stored objects before collection is triggered.
      private const int PurgeThreshholdBase = 20;

      // This is the reciprocal of the desired number of valid (not GCed)
      //    objects in the store before we trigger our own collection.
      private const double ValidReciprocal = 4d;

      // Inverse damping constant
      private const double MU = 0.5d;

      private int _purgeThreshhold = PurgeThreshholdBase;

      private readonly Random random;

      // Lock for synchronizing access to the Config dictionary.
      // The lock does not need to be disposed since WeakDictionary's lifetime
      //    is tied to that of the AppDomain.
      private readonly ReaderWriterLockSlim _locker;
      private Dictionary<int, WeakContainer> _dictionary;

      public WeakDictionary() {
         random = new Random();
         _locker = new ReaderWriterLockSlim();
         _dictionary = new Dictionary<int, WeakContainer>();
      }

      public bool TryGet(int handle, out T obj) {
         WeakContainer item;
         obj = null;

         _locker.EnterReadLock();

         try {
            return _dictionary.TryGetValue(handle, out item) &&
               (obj = item.Reference) != null;
         } finally {
            _locker.ExitReadLock();
         }
      }

      // Add a new config to the data store
      public int Add(string path, T obj) {
         int key;

         _locker.EnterUpgradeableReadLock();
         try {
            _checkMemory();

            do {
               key = random.Next();
            } while (_dictionary.ContainsKey(key));

            _locker.EnterWriteLock();

            try {
               _dictionary.Add(key, new WeakContainer(obj) {
                  Path = path,
               });
            } finally {
               _locker.ExitWriteLock();
            }
         } finally {
            _locker.ExitUpgradeableReadLock();
         }

         return key;
      }

      /// <summary>
      /// Checks if the WeakDictionary contains a Config corresponding to
      ///   the specified handle.
      /// </summary>
      /// <param name="handle">The handle which may or may not correspond to a Config in the WeakDictionary.</param>
      /// <returns>True if handle corresponds to a Config in the WeakDictionary, false otherwise.</returns>
      public bool Contains(int handle) {
         _locker.EnterReadLock();
         var doesContain = _dictionary.ContainsKey(handle);
         _locker.ExitReadLock();
         return doesContain;
      }

      /// <summary>
      /// Gets the last save path for the item specified by the given handle.
      /// </summary>
      /// <param name="handle">Handle to the item whose path is being requested.</param>
      /// <returns>The last known path for the item, 
      ///   or String.Empty if the item could not be found.</returns>
      public string GetPath(int handle) {
         WeakContainer item;
         string retVal = "";

         _locker.EnterReadLock();
         if (_dictionary.TryGetValue(handle, out item) && item.Reference != null)
            retVal = item.Path;

         _locker.ExitReadLock();
         return retVal;
      }

      /// <summary>
      /// Sets the last save path for the item specified by the given handle.
      /// </summary>
      /// <param name="handle">Handle to the item whose path may be overwritten.</param>
      /// <param name="path">The path to write to the item.</param>
      /// <returns>True if the items was in the dictionary and was alive, false otherwise.</returns>
      public bool SetPath(int handle, string path) {
         WeakContainer item;
         bool retVal = false;

         _locker.EnterReadLock();
         if (_dictionary.TryGetValue(handle, out item) && item.Reference != null) {
            item.Path = path;
            retVal = true;
         }

         _locker.ExitReadLock();
         return retVal;
      }

      /// <summary>
      /// Checks to see if the dictionary size is above the purge threshhold.
      /// If it is, the null entries are removed.
      /// 
      /// Either way, the threshhold is adjusted.

      /// </summary>
      private void _checkMemory() {
         // Purge the GC'ed configs from the dictionary
         if (_dictionary.Count >= _purgeThreshhold) {
            var newItems = new Dictionary<int, WeakContainer>(_dictionary.Count);
            foreach (var pair in _dictionary) {
               if (pair.Value.Reference != null) {
                  // The Config has not been garbage collected
                  newItems.Add(pair.Key, pair.Value);
               }
            }

            // Adjust the adaptive count
            // We want to purge when the fraction of valid items is ValidRatio.

            // Let N = _configDictionary.Count, total entries in the dictionary
            // Let M = entries which are invalid
            // Let T_old = _purgeThreshhold before adjustment, the old purge threshhold
            // Let T_new = _purgeThreshhold after adjustment, the new purge threshhold 
            // Let Mu = MU, the tuned constant which is used to tuned purgeThreshhold tracking 
            // Let R = ValidRatio, the desired ratio of valid items for the dictionary.
            //
            // So (N - M) is the number of dictionary entries which are valid
            // We would like purging to trigger when:
            //    (N - M) = R * N, or N = (N - M) / R
            //
            // That means we want T_new = (N - M) / R.

            // We are just going to use a one-tap FIR filter with parameter Mu:
            //    T_new = T_old + (((N - M) / R) - T_old) * Mu
            //
            // Also keep a minimum threshhold:
            //    T_new = Min(T_old + (((N - M) / R) - T_old) * Mu, PurgeThreshholdBase)
            //
            _purgeThreshhold += (int)Math.Floor(
               ((newItems.Count * ValidReciprocal) - _purgeThreshhold)
                  * MU
               );

            if (_purgeThreshhold < PurgeThreshholdBase)
               _purgeThreshhold = PurgeThreshholdBase;

            //////////////////////////////////////////////////////////////////////////////////

            // Swap for the new dictionary if we removed any values
            if (newItems.Count != _dictionary.Count) {
               _locker.EnterWriteLock();

               try {
                  _dictionary = newItems;
               } finally {
                  _locker.ExitWriteLock();
               }
            }
         } else {
            // Receed to stabilize
            if (_purgeThreshhold > PurgeThreshholdBase)
               --_purgeThreshhold;
         }
      }

      /// <summary>
      /// Class to store Configs and their metadata.
      /// 
      /// This class is thread safe.
      /// </summary>
      private class WeakContainer
      {
         // current path of the config file.
         // null means the path is unknown or nonexistent
         public string Path = null;

         private readonly WeakReference _ref;

         // Strong reference to keep the Object alive once at init
         private T _strongReference;

         public WeakContainer(T obj) {
            System.Diagnostics.Debug.Assert(obj != null, "We should not be storing null Configs");

            _strongReference = obj;
            _ref = new WeakReference(obj);
         }

         /// <summary>
         /// Gets the stored config object, or null if the 
         ///   object has been garbage collected.
         /// </summary>
         public T Reference {
            get {
               if (_strongReference != null)
                  _strongReference = null;
               return (T)_ref.Target;
            }
         }
      }
   
   }
}
