using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Idb.Libraries.Abstraction.Services.Cache
{
    public interface IEnvelopeDictionaryCaheManager<TKey,TValue> : IDictionary<TKey,TValue>
    {

    }
}
