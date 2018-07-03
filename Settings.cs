using System.IO;

namespace WeaponVariance
{
    public class Settings
    {
        public bool debug = false;

        public string[] variancePerShot = { };
        public string[] VariancePerShot => variancePerShot;
    }
}