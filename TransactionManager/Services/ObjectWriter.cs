using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TransactionManager.Abstraction.Serialization;

namespace TransactionManager.Services
{
    internal class ObjectWriter(IObjectSerializer serializer): IObjectWriter
    {

        public T WriteObject<T>(T obj)
        {
            if(serializer is null)
            {
                throw new ArgumentNullException(nameof(IObjectSerializer), $"No {nameof(IObjectSerializer)} reigistered in DI container.");
            }

            if (obj is null)
            {
                throw new ArgumentNullException(nameof(obj), "Object to write cannot be null.");
            }

        }
    }
}
