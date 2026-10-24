namespace GhostyNav;

/// <summary>
/// Damped spring integrated per frame. Unlike a fixed-duration storyboard it keeps its
/// velocity when the target changes mid-flight, so interrupted expand/collapse stays smooth.
/// </summary>
public sealed class Spring(double value, double stiffness = 380, double damping = 30)
{
    public double Value = value;
    public double Target = value;
    public double Velocity;
    public double Stiffness = stiffness;
    public double Damping = damping;

    public bool IsSettled => Velocity == 0 && Value == Target;

    public void Snap(double value)
    {
        Value = Target = value;
        Velocity = 0;
    }

    /// <returns>true while the spring is still moving.</returns>
    public bool Step(double dt)
    {
        if (IsSettled) return false;

        // Fixed sub-steps keep semi-implicit Euler stable even after a long frame.
        const double maxStep = 1.0 / 240;
        while (dt > 0)
        {
            double h = Math.Min(dt, maxStep);
            double force = -Stiffness * (Value - Target) - Damping * Velocity;
            Velocity += force * h;
            Value += Velocity * h;
            dt -= h;
        }

        if (Math.Abs(Velocity) < 0.01 && Math.Abs(Value - Target) < 0.01)
        {
            Snap(Target);
            return false;
        }
        return true;
    }
}
