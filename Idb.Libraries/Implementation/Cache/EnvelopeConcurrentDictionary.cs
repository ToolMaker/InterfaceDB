using Idb.Libraries.Abstraction.Services.Cache;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Idb.Libraries.Implementation.Cache
{
    public class EnvelopeConcurrentDictionary<TKey, TValue> : ConcurrentDictionary<TKey,TValue>,IEnvelopeDictionaryCaheManager<TKey,TValue>
    {
    }
}
