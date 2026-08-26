using System;
using System.Collections.Generic;
using MarineEnvironment.Models;

namespace MarineEnvironment.Sources.Fes2014
{
    internal static class Fes2014Harmonics
    {
        private const double Pi = Math.PI;
        private const double HalfPi = Math.PI / 2.0;
        private const double TwoPi = Math.PI * 2.0;

        private static readonly Dictionary<string, WaveDefinition> Waves = BuildDefinitions();

        public static HarmonicState Calculate(string constituentName, DateTime dateTime)
        {
            if (!Waves.TryGetValue(constituentName, out var wave))
                throw new NotSupportedException($"FES2014 constituent '{constituentName}' is not supported by the harmonic synthesizer.");

            var angles = AstronomicAngles.Create(dateTime);
            var v = (wave.T * angles.T)
                    + (wave.S * angles.S)
                    + (wave.H * angles.H)
                    + (wave.P * angles.P)
                    + (wave.P1 * angles.P1)
                    + (wave.Shift * HalfPi);
            var u = (wave.Xi * angles.Xi)
                    + (wave.Nu * angles.Nu)
                    + (wave.NuPrim * angles.NuPrim)
                    + (wave.NuSec * angles.NuSec);
            if (wave.SubtractR)
                u -= angles.R;

            return new HarmonicState(wave.Factor(angles), NormalizeRadians(v + u));
        }

        private static Dictionary<string, WaveDefinition> BuildDefinitions()
        {
            var x = new Dictionary<string, WaveDefinition>(StringComparer.OrdinalIgnoreCase);
            void Add(string name, int t = 0, int s = 0, int h = 0, int p = 0, int p1 = 0,
                     int shift = 0, int xi = 0, int nu = 0, int nuPrim = 0, int nuSec = 0,
                     FactorKind factor = FactorKind.One, bool subtractR = false)
            {
                x[name] = new WaveDefinition(t, s, h, p, p1, shift, xi, nu, nuPrim, nuSec, factor, subtractR);
            }

            Add("Mm", s: 1, p: -1, factor: FactorKind.Mm);
            Add("Mf", s: 2, xi: -2, factor: FactorKind.Mf);
            Add("Mtm", s: 3, p: -1, xi: -2, factor: FactorKind.Mf);
            Add("Msqm", s: 4, h: -2, xi: -2, factor: FactorKind.Mf);
            Add("Ssa", h: 2);
            Add("Sa", h: 1);

            Add("Q1", t: 1, s: -3, h: 1, p: 1, shift: 1, xi: 2, nu: -1, factor: FactorKind.O1);
            Add("O1", t: 1, s: -2, h: 1, shift: 1, xi: 2, nu: -1, factor: FactorKind.O1);
            Add("P1", t: 1, h: -1, shift: 1);
            Add("S1", t: 1);
            Add("K1", t: 1, h: 1, shift: -1, nuPrim: -1, factor: FactorKind.K1);
            Add("J1", t: 1, s: 1, h: 1, p: -1, shift: -1, nu: -1, factor: FactorKind.J1);

            Add("Eps2", t: 2, s: -5, h: 4, p: 1, xi: 2, nu: -2, factor: FactorKind.M2);
            Add("2N2", t: 2, s: -4, h: 2, p: 2, xi: 2, nu: -2, factor: FactorKind.M2);
            Add("Mu2", t: 2, s: -4, h: 4, xi: 2, nu: -2, factor: FactorKind.M2);
            Add("N2", t: 2, s: -3, h: 2, p: 1, xi: 2, nu: -2, factor: FactorKind.M2);
            Add("Nu2", t: 2, s: -3, h: 4, p: -1, xi: 2, nu: -2, factor: FactorKind.M2);
            Add("M2", t: 2, s: -2, h: 2, xi: 2, nu: -2, factor: FactorKind.M2);
            Add("MKS2", t: 2, s: -2, h: 4, xi: 2, nu: -2, nuSec: -2, factor: FactorKind.M2K2);
            Add("Lambda2", t: 2, s: -1, p: 1, shift: 2, xi: 2, nu: -2, factor: FactorKind.M2);
            Add("L2", t: 2, s: -1, h: 2, p: -1, shift: 2, xi: 2, nu: -2, factor: FactorKind.L2, subtractR: true);
            Add("T2", t: 2, h: -1, p1: 1);
            Add("S2", t: 2);
            Add("R2", t: 2, h: 1, p1: -1, shift: 2);
            Add("K2", t: 2, h: 2, nuSec: -2, factor: FactorKind.K2);

            Add("M3", t: 3, s: -3, h: 3, xi: 3, nu: -3, factor: FactorKind.M3);
            Add("N4", t: 4, s: -6, h: 4, p: 2, xi: 4, nu: -4, factor: FactorKind.M22);
            Add("MN4", t: 4, s: -5, h: 4, p: 1, xi: 4, nu: -4, factor: FactorKind.M22);
            Add("M4", t: 4, s: -4, h: 4, xi: 4, nu: -4, factor: FactorKind.M22);
            Add("MS4", t: 4, s: -2, h: 2, xi: 2, nu: -2, factor: FactorKind.M2);
            Add("S4", t: 4);
            Add("M6", t: 6, s: -6, h: 6, xi: 6, nu: -6, factor: FactorKind.M23);
            Add("M8", t: 8, s: -8, h: 8, xi: 8, nu: -8, factor: FactorKind.M24);
            Add("MSf", s: 2, h: -2, xi: 2, nu: -2, factor: FactorKind.M2);
            return x;
        }

        private static double NormalizeRadians(double value)
        {
            value %= TwoPi;
            return value < 0 ? value + TwoPi : value;
        }

        internal readonly struct HarmonicState
        {
            public HarmonicState(double factor, double argument)
            {
                Factor = factor;
                Argument = argument;
            }
            public double Factor { get; }
            public double Argument { get; }
        }

        private enum FactorKind { One, Mm, Mf, O1, J1, M2, M3, K1, K2, M22, M23, M24, L2, M2K2 }

        private sealed class WaveDefinition
        {
            public WaveDefinition(int t, int s, int h, int p, int p1, int shift, int xi, int nu,
                                  int nuPrim, int nuSec, FactorKind factor, bool subtractR)
            {
                T=t; S=s; H=h; P=p; P1=p1; Shift=shift; Xi=xi; Nu=nu; NuPrim=nuPrim; NuSec=nuSec;
                FactorKind=factor; SubtractR=subtractR;
            }
            public int T { get; } public int S { get; } public int H { get; } public int P { get; }
            public int P1 { get; } public int Shift { get; } public int Xi { get; } public int Nu { get; }
            public int NuPrim { get; } public int NuSec { get; } public FactorKind FactorKind { get; }
            public bool SubtractR { get; }
            public double Factor(AstronomicAngles a)
            {
                switch (FactorKind)
                {
                    case FactorKind.One: return 1.0;
                    case FactorKind.Mm: return (2.0/3.0-Math.Pow(Math.Sin(a.I),2))/0.5021;
                    case FactorKind.Mf: return Math.Pow(Math.Sin(a.I),2)/0.1578;
                    case FactorKind.O1: return Math.Sin(a.I)*Math.Pow(Math.Cos(a.I/2),2)/0.3800;
                    case FactorKind.J1: return Math.Sin(2*a.I)/0.7214;
                    case FactorKind.M2: return Math.Pow(Math.Cos(a.I/2),4)/0.9154;
                    case FactorKind.M3: return Math.Pow(Math.Cos(a.I/2),6)/0.8758;
                    case FactorKind.K1:
                        var s2=Math.Sin(2*a.I); return Math.Sqrt(0.8965*s2*s2+0.6001*s2*Math.Cos(a.Nu)+0.1006);
                    case FactorKind.K2:
                        var si2=Math.Pow(Math.Sin(a.I),2); return Math.Sqrt(19.0444*si2*si2+2.7702*si2*Math.Cos(2*a.Nu)+0.0981);
                    case FactorKind.M22: var m2= Math.Pow(Math.Cos(a.I/2),4)/0.9154; return m2*m2;
                    case FactorKind.M23: var m23=Math.Pow(Math.Cos(a.I/2),4)/0.9154; return m23*m23*m23;
                    case FactorKind.M24: var m24=Math.Pow(Math.Cos(a.I/2),4)/0.9154; return Math.Pow(m24,4);
                    case FactorKind.L2: var ml=Math.Pow(Math.Cos(a.I/2),4)/0.9154; return ml*a.X1Ra;
                    case FactorKind.M2K2:
                        var mm=Math.Pow(Math.Cos(a.I/2),4)/0.9154; var ss=Math.Pow(Math.Sin(a.I),2);
                        var kk=Math.Sqrt(19.0444*ss*ss+2.7702*ss*Math.Cos(2*a.Nu)+0.0981); return mm*kk;
                    default: throw new ArgumentOutOfRangeException();
                }
            }
        }

        private sealed class AstronomicAngles
        {
            public double T, N, H, S, P1, P, I, Xi, Nu, X1Ra, R, NuPrim, NuSec;

            public static AstronomicAngles Create(DateTime dateTime)
            {
                var utc = dateTime.Kind == DateTimeKind.Utc ? dateTime : dateTime.ToUniversalTime();
                var epoch = (utc - new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc)).TotalSeconds;
                const double secondsPerDay=86400.0;
                var jc=((epoch/secondsPerDay)+25567.5)/36525.0;
                var a=new AstronomicAngles();
                a.N = Dms(259,10,57.12) + jc * (-(5*360 + Dms(0,0,482912.63)));
                a.H = Dms(279,41,48.04) + jc * Dms(0,0,129602768.13);
                a.S = Dms(270,26,14.72) + jc * (1336*360 + Dms(0,0,1108411.20));
                a.P1= Dms(281,13,15) + jc * Dms(0,0,6189.03);
                a.P = Dms(334,19,40.87) + jc * (11*360 + Dms(0,0,392515.94));
                var daySeconds=epoch%secondsPerDay; if(daySeconds<0) daySeconds+=secondsPerDay;
                a.T=Math.IEEERemainder(180.0+15.0*(daySeconds/3600.0),360.0);
                a.T=Rad(a.T); a.N=Rad(NormDeg(a.N)); a.H=Rad(NormDeg(a.H)); a.S=Rad(NormDeg(a.S)); a.P=Rad(NormDeg(a.P)); a.P1=Rad(NormDeg(a.P1));

                var cosI=0.91370-(0.03569*Math.Cos(a.N));
                a.I=Math.Acos(cosI);
                var tgn2=Math.Tan(a.N/2.0);
                var at1=Math.Atan(1.01883*tgn2);
                var at2=Math.Atan(0.64412*tgn2);
                a.Xi=-at1-at2+a.N; if(a.N>Pi) a.Xi-=TwoPi;
                a.Nu=at1-at2;
                var tgi2=Math.Pow(Math.Tan(a.I/2.0),2);
                var pRel=a.P-a.Xi;
                a.X1Ra=Math.Sqrt(1.0+tgi2*(36.0*tgi2-12.0*Math.Cos(2.0*pRel)));
                a.R=Math.Atan(Math.Sin(2.0*pRel)/(1.0/(6.0*tgi2)-Math.Cos(2.0*pRel)));
                a.NuPrim=Math.Atan(Math.Sin(2.0*a.I)*Math.Sin(a.Nu)/(Math.Sin(2.0*a.I)*Math.Cos(a.Nu)+0.3347));
                var sinI2=Math.Pow(Math.Sin(a.I),2);
                a.NuSec=0.5*Math.Atan((sinI2*Math.Sin(2.0*a.Nu))/(sinI2*Math.Cos(2.0*a.Nu)+0.0727));
                return a;
            }
            private static double Dms(double d,double m,double s)=>d+(m/60.0)+(s/3600.0);
            private static double Rad(double d)=>d*Pi/180.0;
            private static double NormDeg(double d){d%=360.0;return d<0?d+360.0:d;}
        }
    }
}
