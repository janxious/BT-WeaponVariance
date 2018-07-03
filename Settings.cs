using System.IO;

namespace WeaponVariance
{
    public class Settings
    {
        public bool debug = false;

        public float varianceStandardDeviation = 7.0f;
        public float VarianceStandardDeviation => varianceStandardDeviation;
    }
}