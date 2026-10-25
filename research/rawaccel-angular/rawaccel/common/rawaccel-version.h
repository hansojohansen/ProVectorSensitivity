#pragma once

#define RA_VER_MAJOR 1
#define RA_VER_MINOR 7
// OpenVector RESEARCH build: patch 900 marks the angle x speed gain table ABI.
// The settings blob layout differs from upstream 1.7.x, so the version must differ too:
//  - stock 1.7.x writer/GUI + this driver  -> "newer driver is installed" (refuses, safe)
//  - this writer + stock 1.7.x driver      -> "reinstallation required" (refuses, safe)
// The driver additionally rejects any WRITE whose size does not match its own struct sizes.
#define RA_VER_PATCH 900
#define RA_RESEARCH_BUILD 1

#define RA_OS "Win10+"

#define RA_M_STR_HELPER(x) #x
#define RA_M_STR(x) RA_M_STR_HELPER(x)

#define RA_VER_STRING RA_M_STR(RA_VER_MAJOR) "." RA_M_STR(RA_VER_MINOR) "." RA_M_STR(RA_VER_PATCH)

namespace rawaccel {

    struct version_t {
        int major;
        int minor;
        int patch;
    };

    constexpr bool operator<(const version_t& lhs, const version_t& rhs)
    {
        return (lhs.major != rhs.major) ?
               (lhs.major < rhs.major)  :
               (lhs.minor != rhs.minor) ?
               (lhs.minor < rhs.minor)  :
               (lhs.patch < rhs.patch)  ;
    }

    inline constexpr version_t version = { RA_VER_MAJOR, RA_VER_MINOR, RA_VER_PATCH };
#ifndef _KERNEL_MODE
    inline constexpr version_t min_driver_version = { 1, 7, 900 }; // RESEARCH build: same ABI only
#endif

}
