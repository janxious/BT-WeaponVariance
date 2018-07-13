namespace WeaponVariance
{
    public class Settings
    {
        public bool debug = false;

        public float standardDeviationPercentOfVariance = 75.0f;
        public float StandardDeviationVarianceMultiplier => standardDeviationPercentOfVariance / 100.0f;
    }
}