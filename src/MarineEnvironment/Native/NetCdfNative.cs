using System;
using System.IO;
using System.Runtime.InteropServices;

namespace MarineEnvironment.Native
{
    internal static class NetCdfNative
    {
        private const string LibraryName = "netcdf";
        internal const int NoError = 0;
        internal const int Nowrite = 0;

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int nc_open(string path, int mode, out int ncidp);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int nc_close(int ncid);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int nc_inq_varid(int ncid, string name, out int varidp);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int nc_inq_varndims(int ncid, int varid, out int ndimsp);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int nc_inq_vardimid(int ncid, int varid, [Out] int[] dimidsp);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int nc_inq_dimlen(int ncid, int dimid, out UIntPtr lenp);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int nc_get_var_double(int ncid, int varid, [Out] double[] value);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int nc_get_var1_double(int ncid, int varid, UIntPtr[] indexp, out double value);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int nc_get_att_double(int ncid, int varid, string name, out double value);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr nc_strerror(int ncerr);

        internal static string ErrorText(int code)
        {
            var ptr = nc_strerror(code);
            return ptr == IntPtr.Zero ? $"NetCDF error {code}" : Marshal.PtrToStringAnsi(ptr) ?? $"NetCDF error {code}";
        }

        internal static void ThrowIfError(int code, string operation)
        {
            if (code == NoError)
                return;

            throw new InvalidDataException($"{operation}: {ErrorText(code)} ({code})");
        }
    }
}
