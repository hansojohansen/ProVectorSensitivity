// OpenVector RESEARCH build - lets the MSVC-oriented Raw Accel headers compile with g++/clang++.
// Force-included by run.sh (-include host_shim.h). Not used by the Windows build.
#pragma once
#include <cmath>
#define __forceinline inline
#define _copysign copysign
namespace rawaccel {}
namespace ra = rawaccel;   // the driver and wrapper define this alias before including the headers
