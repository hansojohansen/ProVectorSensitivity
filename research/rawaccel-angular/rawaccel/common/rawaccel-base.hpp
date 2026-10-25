#pragma once

#include "math-vec2.hpp"

namespace rawaccel {
    using milliseconds = double;

    inline constexpr int POLL_RATE_MIN = 125;
    inline constexpr int POLL_RATE_MAX = 8000;

    inline constexpr milliseconds DEFAULT_TIME_MIN = 1000.0 / POLL_RATE_MAX / 2;
    inline constexpr milliseconds DEFAULT_TIME_MAX = 100;

    inline constexpr milliseconds WRITE_DELAY = 1000;

    inline constexpr size_t MAX_DEV_ID_LEN = 200;
    inline constexpr size_t MAX_NAME_LEN = 256;

    inline constexpr size_t LUT_RAW_DATA_CAPACITY = 514;
    inline constexpr size_t LUT_POINTS_CAPACITY = LUT_RAW_DATA_CAPACITY / 2;

    inline constexpr double MAX_NORM = 16;

    // ---- OpenVector RESEARCH build: angle x speed gain table -------------------
    // Not part of upstream Raw Accel. See research/rawaccel-angular/README.md.
    inline constexpr int ANGULAR_GAIN_MAX_ANGLES = 128;
    inline constexpr int ANGULAR_GAIN_MAX_SPEEDS = 16;
    inline constexpr size_t ANGULAR_GAIN_CAPACITY =
        size_t(ANGULAR_GAIN_MAX_ANGLES) * ANGULAR_GAIN_MAX_SPEEDS;
    inline constexpr double ANGULAR_GAIN_MIN = 0.5;
    inline constexpr double ANGULAR_GAIN_MAX = 2.0;

    // At this DPI, one count per ms equals one inch per second.
    inline constexpr double NORMALIZED_DPI = 1000;

    inline constexpr bool LEGACY = 0;
    inline constexpr bool GAIN = 1;
    
    enum class accel_mode {
        classic,
        jump,
        natural,
        synchronous,
        power,
        lookup,
        noaccel
    };

    enum class cap_mode {
        io, in, out
    };

    struct accel_args {
        accel_mode mode = accel_mode::noaccel;
        bool gain = 1;

        double input_offset = 0;
        double output_offset = 0;
        double acceleration = 0.005;
        double decay_rate = 0.1;
        double gamma = 1;
        double motivity = 1.5;
        double exponent_classic = 2;
        double scale = 1;
        double exponent_power = 0.05;
        double limit = 1.5;
        double sync_speed = 5;
        double smooth = 0.5;

        vec2d cap = { 15, 1.5 };
        cap_mode cap_mode = cap_mode::out;

        int length = 0;
        mutable float data[LUT_RAW_DATA_CAPACITY] = {};
    };

    struct speed_args
    {
        bool whole = true;
        double lp_norm = 2;
        double input_speed_smooth_halflife = 0;
        double scale_smooth_halflife = 0;
        double output_speed_smooth_halflife = 0;
    };

    /// <summary>
    /// RESEARCH ONLY. Static, direction-preserving gain table g(theta, speed).
    ///
    /// theta = atan2(dy, dx) of the raw input packet, in [0, 2pi):
    ///   0 = right, pi/2 = down, pi = left, 3pi/2 = up (Windows/Raw Input convention).
    /// Angle grid:  theta_i = 2pi * i / angle_points, i = 0 .. angle_points-1 (periodic).
    /// Speed grid:  log-spaced, s_j = speed_min * (speed_max/speed_min)^(j/(speed_points-1)),
    ///              j = 0 .. speed_points-1; speed in Raw Accel input units (counts/ms,
    ///              times 1000/DPI when the device DPI is set). speed_points = 1 means
    ///              speed-independent (speed_min/speed_max are then ignored).
    /// data[j * angle_points + i] = gain at (theta_i, s_j), each in [0.5, 2].
    /// Lookup: bilinear, periodic in theta, linear in log(speed), clamped at the speed ends.
    /// angle_points == 0 disables the table (the default).
    /// </summary>
    struct angular_gain_args {
        int angle_points = 0;
        int speed_points = 0;
        double speed_min = 1;
        double speed_max = 1;
        int length = 0;   // number of data entries provided (validation: == angle_points * speed_points)
        float data[ANGULAR_GAIN_CAPACITY] = {};
    };

    struct profile {
        wchar_t name[MAX_NAME_LEN] = L"default";

        vec2d domain_weights = { 1, 1 };
        vec2d range_weights = { 1, 1 };

        accel_args accel_x;
        accel_args accel_y;
        speed_args speed_processor_args;

        double output_dpi = NORMALIZED_DPI;
        double yx_output_dpi_ratio = 1;
        double lr_output_dpi_ratio = 1;
        double ud_output_dpi_ratio = 1;

        double degrees_rotation = 0;

        double degrees_snap = 0;

        double speed_min = 0;
        double speed_max = 0;

        // RESEARCH build only (appended so the upstream fields keep their order).
        angular_gain_args angular_gain;
    };
}
