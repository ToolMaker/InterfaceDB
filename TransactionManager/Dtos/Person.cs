using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TransactionManager.Attributes;

namespace TransactionManager.Dtos
{
    [StorageScope()]
    public class Person : HumanBeing, IPerson
    {
        public IFingers Fingers { get; set; }
    }
    [StorageScope()]
    public class Alien : HumanBeing, IPerson
    {
        public IFingers Fingers { get; set; }
    }
}
