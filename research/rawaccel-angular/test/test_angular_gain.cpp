// OpenVector RESEARCH build - host-side unit tests for the angle x speed gain table.
//
// Compiles the real driver math (rawaccel/common/*.hpp, the same headers the kernel driver
// and the wrapper include) with g++/clang++ on Linux. No Windows, WDK or driver needed.
// Run via ./run.sh. Exit code 0 = all tests passed.

#include "rawaccel.hpp"
#include "rawaccel-validate.hpp"

#include <cmath>
#include <cstdio>
#include <cstring>
#include <random>
#include <string>
#include <vector>

namespace ra = rawaccel;

static int g_failed = 0;
static int g_checks = 0;

#define CHECK(cond, ...)                                                   \
    do {                                                                   \
        ++g_checks;                                                        \
        if (!(cond)) {                                                     \
            ++g_failed;                                                    \
            std::printf("  FAIL %s:%d: %s :: ", __FILE__, __LINE__, #cond); \
            std::printf(__VA_ARGS__);                                      \
            std::printf("\n");                                             \
        }                                                                  \
    } while (0)

static bool near(double a, double b, double tol) { return std::fabs(a - b) <= tol; }

static constexpr double PI = 3.14159265358979323846;

// ---------------------------------------------------------------------------------------
// helpers

struct rig {
    ra::modifier_settings settings;
    ra::modifier mod;
    ra::speed_processor sp;

    explicit rig(const ra::profile& p)
    {
        settings.prof = p;
        ra::init_data(settings);
        mod = ra::modifier(settings);
        sp.init(settings.prof.speed_processor_args);
    }

    vec2d run(double x, double y, double dpi_factor = 1, double time = 1)
    {
        vec2d v = { x, y };
        mod.modify(v, sp, settings, dpi_factor, time);
        return v;
    }
};

static void set_table(ra::profile& p, int n, int m, double smin, double smax,
                      const std::vector<float>& gains)
{
    p.angular_gain.angle_points = n;
    p.angular_gain.speed_points = m;
    p.angular_gain.speed_min = smin;
    p.angular_gain.speed_max = smax;
    p.angular_gain.length = int(gains.size());
    for (size_t i = 0; i < gains.size() && i < ra::ANGULAR_GAIN_CAPACITY; i++) {
        p.angular_gain.data[i] = gains[i];
    }
}

static int count_errors(const ra::profile& p, std::vector<std::string>* msgs = nullptr)
{
    auto ret = ra::valid(p, [&](const char* m) { if (msgs) msgs->push_back(m); });
    return ret.count;
}

// gain magnitude and direction change of one packet through the modifier
static void ratio_and_turn(const vec2d& in, const vec2d& out, double& ratio, double& turn)
{
    ratio = magnitude(out) / magnitude(in);
    turn = std::atan2(in.x * out.y - in.y * out.x, in.x * out.x + in.y * out.y);
}

// smooth periodic test profile (harmonics 1..3, includes the sin 2theta term stock Raw Accel can't do)
static double smooth_target(double th)
{
    return 1.0 + 0.06 * std::cos(th) - 0.04 * std::sin(th) + 0.05 * std::cos(2 * th)
         + 0.07 * std::sin(2 * th) + 0.02 * std::cos(3 * th);
}

// ---------------------------------------------------------------------------------------
// tests

static void test_disabled_is_identity()
{
    std::printf("[disabled table leaves output unchanged]\n");
    ra::profile p;
    rig r(p);
    CHECK(!r.settings.data.flags.apply_angular_gain, "flag should be off by default");
    CHECK(count_errors(p) == 0, "default profile must validate");
    for (int x = -20; x <= 20; x += 3) {
        for (int y = -20; y <= 20; y += 4) {
            if (!x && !y) continue;
            vec2d o = r.run(x, y);
            CHECK(o.x == x && o.y == y, "(%d,%d) -> (%g,%g)", x, y, o.x, o.y);
        }
    }
}

static void test_grid_nodes_exact()
{
    std::printf("[gain at grid nodes equals table value; D14 angle convention]\n");
    // N = 8: nodes at 0 (right), 45, 90 (down), 135, 180 (left), 225, 270 (up), 315 degrees
    std::vector<float> g = { 1.10f, 0.90f, 1.20f, 0.80f, 1.30f, 0.70f, 1.40f, 0.60f };
    ra::profile p;
    set_table(p, 8, 1, 1, 1, g);
    CHECK(count_errors(p) == 0, "table must validate");
    rig r(p);
    struct { int x, y, idx; } cases[] = {
        { 100, 0, 0 },   // right
        { 50, 50, 1 },   // right-down
        { 0, 100, 2 },   // down  (+y is down in Windows mouse counts)
        { -50, 50, 3 },
        { -100, 0, 4 },  // left
        { -50, -50, 5 },
        { 0, -100, 6 },  // up
        { 50, -50, 7 },
    };
    for (auto& c : cases) {
        vec2d o = r.run(c.x, c.y);
        double ratio, turn;
        ratio_and_turn({ double(c.x), double(c.y) }, o, ratio, turn);
        CHECK(near(ratio, g[c.idx], 1e-6), "(%d,%d) ratio %.9f expected %.9f", c.x, c.y, ratio, double(g[c.idx]));
        CHECK(near(turn, 0, 1e-12), "(%d,%d) direction changed by %g rad", c.x, c.y, turn);
    }
}

static void test_linear_interp_and_wrap()
{
    std::printf("[linear interpolation in angle, periodic wrap at 2pi]\n");
    std::vector<float> g = { 1.0f, 1.5f, 0.5f, 2.0f };   // nodes 0, 90, 180, 270 deg
    ra::profile p;
    set_table(p, 4, 1, 1, 1, g);
    rig r(p);

    // midway right/down -> average of nodes 0 and 1
    vec2d o = r.run(100, 100);
    CHECK(near(magnitude(o) / std::hypot(100, 100), 1.25, 1e-9), "45deg got %.9f", magnitude(o) / std::hypot(100, 100));

    // arbitrary angle: atan2(1, 3) = 18.43 deg -> 1 + 0.5 * (18.43/90)
    double th = std::atan2(1.0, 3.0);
    o = r.run(3, 1);
    double expect = 1.0 + 0.5 * (th / (PI / 2));
    CHECK(near(magnitude(o) / std::hypot(3, 1), expect, 1e-9), "18.4deg got %.9f expected %.9f", magnitude(o) / std::hypot(3, 1), expect);

    // between last node (270 = up, 2.0) and first node (0/360 = right, 1.0): 315 deg -> 1.5
    o = r.run(100, -100);
    CHECK(near(magnitude(o) / std::hypot(100, 100), 1.5, 1e-9), "315deg got %.9f", magnitude(o) / std::hypot(100, 100));

    // just below 360 deg tends to node 0; continuity across the seam
    o = r.run(100000, -1);
    double just_below = magnitude(o) / std::hypot(100000, 1);
    o = r.run(100000, 1);
    double just_above = magnitude(o) / std::hypot(100000, 1);
    CHECK(near(just_below, 1.0, 1e-4) && near(just_above, 1.0, 1e-4), "seam %.9f / %.9f", just_below, just_above);

    // direct lookup at theta = exactly 2pi - tiny epsilon and huge inputs stays in range
    double gl = ra::angular_gain_lookup(p.angular_gain, 1, -1e-300, 1);
    CHECK(near(gl, 1.0, 1e-9), "2pi-eps lookup %.12f", gl);

    // N = 1 means constant gain in every direction
    ra::profile q;
    set_table(q, 1, 1, 1, 1, { 1.25f });
    CHECK(count_errors(q) == 0, "N=1 must validate");
    rig rq(q);
    for (double a = 0; a < 2 * PI; a += 0.3) {
        vec2d in = { 1000 * std::cos(a), 1000 * std::sin(a) };
        vec2d out = rq.run(in.x, in.y);
        CHECK(near(magnitude(out) / 1000, 1.25, 1e-9), "N=1 at %g", a);
    }
}

static void test_dense_grid_matches_target()
{
    std::printf("[128-point table reproduces a smooth target incl. sin 2theta]\n");
    const int n = 128;
    std::vector<float> g(n);
    for (int i = 0; i < n; i++) g[i] = float(smooth_target(2 * PI * i / n));
    ra::profile p;
    set_table(p, n, 1, 1, 1, g);
    CHECK(count_errors(p) == 0, "table must validate");
    rig r(p);
    double max_err = 0;
    for (int k = 0; k < 3600; k++) {
        double a = 2 * PI * k / 3600;
        vec2d in = { 1000 * std::cos(a), 1000 * std::sin(a) };
        vec2d out = r.run(in.x, in.y);
        double ratio, turn;
        ratio_and_turn(in, out, ratio, turn);
        max_err = std::fmax(max_err, std::fabs(ratio / smooth_target(a) - 1));
        CHECK(near(turn, 0, 1e-12), "direction changed at %g", a);
    }
    std::printf("  max relative interpolation error, N=128: %.2e\n", max_err);
    CHECK(max_err < 1e-3, "max err %g", max_err);
}

static void test_speed_axis()
{
    std::printf("[speed axis: log-spaced, linear in log speed, clamped]\n");
    // N = 1 so only speed matters. Speeds 1, 10, 100 counts/ms.
    ra::profile p;
    set_table(p, 1, 3, 1, 100, { 0.8f, 1.0f, 1.6f });
    CHECK(count_errors(p) == 0, "table must validate");
    rig r(p);
    auto gain_at = [&](double speed) {
        // packet of length 10 along x; speed = |v| * dpi_factor / time
        double time = 10.0 / speed;
        vec2d o = r.run(10, 0, 1, time);
        return o.x / 10;
    };
    CHECK(near(gain_at(1), 0.8, 1e-6), "s=1 %.9f", gain_at(1));
    CHECK(near(gain_at(10), 1.0, 1e-6), "s=10 %.9f", gain_at(10));
    CHECK(near(gain_at(100), 1.6, 1e-6), "s=100 %.9f", gain_at(100));
    CHECK(near(gain_at(std::sqrt(10.0)), 0.9, 1e-6), "s=sqrt10 %.9f", gain_at(std::sqrt(10.0)));
    CHECK(near(gain_at(std::pow(10.0, 1.75)), 1.45, 1e-6), "s=10^1.75 %.9f", gain_at(std::pow(10.0, 1.75)));
    CHECK(near(gain_at(0.01), 0.8, 1e-6), "below range clamps %.9f", gain_at(0.01));
    CHECK(near(gain_at(1e5), 1.6, 1e-6), "above range clamps %.9f", gain_at(1e5));

    // dpi_factor scales speed exactly like Raw Accel's own input speed
    // (upstream also scales the output by dpi_factor, hence the 0.5)
    vec2d o = r.run(10, 0, 0.5, 0.5);   // speed = 10 * 0.5 / 0.5 = 10 -> gain 1.0
    CHECK(near(o.x / 10, 0.5 * 1.0, 1e-6), "dpi factor %.9f", o.x / 10);

    // bilinear: 2 angles x 2 speeds
    ra::profile q;
    // rows: speed 1: [right 1.0, left 1.2]; speed 4: [right 1.4, left 1.8]
    set_table(q, 2, 2, 1, 4, { 1.0f, 1.2f, 1.4f, 1.8f });
    CHECK(count_errors(q) == 0, "2x2 must validate");
    rig rq(q);
    // down (theta = pi/2) is halfway between node 0 (right) and node 1 (left), speed 2 is halfway in log
    double t = 10.0 / 2.0;
    o = rq.run(0, 10, 1, t);
    double expect = 0.5 * (0.5 * (1.0 + 1.2)) + 0.5 * (0.5 * (1.4 + 1.8));
    CHECK(near(o.y / 10, expect, 1e-6) && o.x == 0, "bilinear %.9f expected %.9f", o.y / 10, expect);
}

static void test_scalar_after_pipeline()
{
    std::printf("[table output == g(raw angle, raw speed) * stock output, random stock settings]\n");
    std::mt19937_64 rng(12345);
    std::uniform_real_distribution<double> U(0, 1);
    const int n = 64, m = 4;
    std::vector<float> g(n * m);
    for (int j = 0; j < m; j++)
        for (int i = 0; i < n; i++)
            g[j * n + i] = float(smooth_target(2 * PI * i / n) * (0.9 + 0.07 * j));

    for (int trial = 0; trial < 200; trial++) {
        ra::profile base;
        base.output_dpi = 400 + 1600 * U(rng);
        base.yx_output_dpi_ratio = 0.8 + 0.4 * U(rng);
        base.lr_output_dpi_ratio = 0.8 + 0.4 * U(rng);
        base.ud_output_dpi_ratio = 0.8 + 0.4 * U(rng);
        base.degrees_rotation = (U(rng) < 0.3) ? -10 + 20 * U(rng) : 0;
        base.degrees_snap = (U(rng) < 0.3) ? 10 * U(rng) : 0;
        base.range_weights = { 0.5 + U(rng), 0.5 + U(rng) };
        base.domain_weights = { 0.5 + U(rng), 0.5 + U(rng) };
        base.speed_processor_args.whole = U(rng) < 0.7;
        if (U(rng) < 0.5) {
            base.accel_x.mode = ra::accel_mode::classic;
            base.accel_x.acceleration = 0.01;
            base.accel_y = base.accel_x;
        }
        ra::profile with = base;
        set_table(with, n, m, 0.5, 50, g);
        if (count_errors(base) != 0 || count_errors(with) != 0) {
            CHECK(false, "random profile invalid (test bug)");
            continue;
        }
        rig rs(base), rt(with);
        for (int k = 0; k < 50; k++) {
            int x = int(U(rng) * 255) - 127, y = int(U(rng) * 255) - 127;
            if (!x && !y) continue;
            double dpi_factor = (U(rng) < 0.5) ? 1 : 1000.0 / 1600;
            double time = 0.125 + 8 * U(rng);
            vec2d s = rs.run(x, y, dpi_factor, time);
            vec2d t = rt.run(x, y, dpi_factor, time);
            double gl = ra::angular_gain_lookup(with.angular_gain, x, y, std::hypot(x, y) * dpi_factor / time);
            CHECK(near(t.x, s.x * gl, 1e-9 * (1 + std::fabs(s.x))) && near(t.y, s.y * gl, 1e-9 * (1 + std::fabs(s.y))),
                  "trial %d (%d,%d): stock (%g,%g) table (%g,%g) g %g", trial, x, y, s.x, s.y, t.x, t.y, gl);
        }
    }
}

static void test_stateless()
{
    std::printf("[stateless: result depends only on the current packet]\n");
    std::vector<float> g(32);
    for (int i = 0; i < 32; i++) g[i] = float(smooth_target(2 * PI * i / 32));
    ra::profile p;
    set_table(p, 32, 1, 1, 1, g);
    rig a(p), b(p);
    // feed b a long unrelated history first
    for (int k = 0; k < 1000; k++) b.run((k % 7) - 3, (k % 5) - 2, 1, 0.125);
    for (int x = -9; x <= 9; x += 2) {
        for (int y = -9; y <= 9; y += 3) {
            vec2d oa = a.run(x, y), ob = b.run(x, y);
            CHECK(oa.x == ob.x && oa.y == ob.y, "(%d,%d) differs after history", x, y);
        }
    }
}

static void test_validation()
{
    std::printf("[validation rejects bad tables]\n");
    auto expect_errors = [](const char* what, auto mutate) {
        ra::profile p;
        set_table(p, 4, 2, 1, 10, { 1, 1, 1, 1, 1, 1, 1, 1 });
        CHECK(count_errors(p) == 0, "baseline");
        mutate(p);
        std::vector<std::string> msgs;
        int n = count_errors(p, &msgs);
        CHECK(n > 0, "%s: expected a validation error", what);
        if (n > 0) std::printf("  ok  %-28s -> \"%s\"\n", what, msgs[0].c_str());
    };
    expect_errors("too many angles", [](ra::profile& p) { p.angular_gain.angle_points = 129; });
    expect_errors("negative angles", [](ra::profile& p) { p.angular_gain.angle_points = -1; });
    expect_errors("zero speeds", [](ra::profile& p) { p.angular_gain.speed_points = 0; });
    expect_errors("too many speeds", [](ra::profile& p) { p.angular_gain.speed_points = 17; });
    expect_errors("length mismatch", [](ra::profile& p) { p.angular_gain.length = 7; });
    expect_errors("gain below 0.5", [](ra::profile& p) { p.angular_gain.data[3] = 0.49f; });
    expect_errors("gain above 2", [](ra::profile& p) { p.angular_gain.data[0] = 2.01f; });
    expect_errors("gain NaN", [](ra::profile& p) { p.angular_gain.data[5] = std::nanf(""); });
    expect_errors("speed_min <= 0", [](ra::profile& p) { p.angular_gain.speed_min = 0; });
    expect_errors("speed_max <= speed_min", [](ra::profile& p) { p.angular_gain.speed_max = 1; });
    expect_errors("gains but table off", [](ra::profile& p) { p.angular_gain.angle_points = 0; });

    // M = 1 ignores the speed range
    ra::profile q;
    set_table(q, 4, 1, 0, 0, { 1, 1.1f, 0.9f, 1 });
    CHECK(count_errors(q) == 0, "M=1 with zero speed range must validate");
    // max size
    ra::profile big;
    set_table(big, 128, 16, 0.1, 200, std::vector<float>(128 * 16, 1.5f));
    CHECK(count_errors(big) == 0, "128x16 must validate");
}

static void test_unvalidated_garbage_is_safe()
{
    std::printf("[kernel-side safety on unvalidated data (no OOB, finite, clamped)]\n");
    std::mt19937_64 rng(777);
    std::uniform_int_distribution<int> I(-300, 300);
    std::uniform_real_distribution<double> U(-1e6, 1e6);
    const float specials[] = { std::nanf(""), INFINITY, -INFINITY, 0.f, -1.f, 1e30f, 1.f };
    for (int trial = 0; trial < 2000; trial++) {
        ra::angular_gain_args t;
        t.angle_points = I(rng);
        t.speed_points = I(rng) / 10;
        t.speed_min = (trial % 3 == 0) ? std::nan("") : U(rng);
        t.speed_max = (trial % 5 == 0) ? INFINITY : U(rng);
        t.length = I(rng);
        for (size_t i = 0; i < ra::ANGULAR_GAIN_CAPACITY; i++) {
            t.data[i] = (i % 11 == 0) ? specials[(i / 11 + trial) % 7] : float(U(rng) / 1e5);
        }
        for (int k = 0; k < 20; k++) {
            double dx = I(rng), dy = I(rng), speed = std::fabs(U(rng)) * ((k % 4 == 0) ? 0 : 1);
            double gl = ra::angular_gain_lookup(t, dx, dy, speed);
            CHECK(!ra::infnan(gl) && gl >= ra::ANGULAR_GAIN_MIN && gl <= ra::ANGULAR_GAIN_MAX,
                  "trial %d: gain %g", trial, gl);
        }
    }
}

// Informational: per-packet angle quantisation (A 2.4 note). A sensor moving in a straight line
// at angle theta produces integer packets; each packet gets the gain of its own (quantised) angle.
// We compare the effective gain over the whole movement with the ideal g(theta).
static void report_quantisation()
{
    std::printf("[INFO per-packet quantisation, N=64 smooth target, 300 ms straight strokes]\n");
    const int n = 64;
    std::vector<float> g(n);
    for (int i = 0; i < n; i++) g[i] = float(smooth_target(2 * PI * i / n));
    ra::profile p;
    set_table(p, n, 1, 1, 1, g);
    rig r(p);

    std::printf("  %-8s %-12s %-14s %-14s %-14s\n", "poll Hz", "counts/ms", "counts/packet", "RMS gain err", "max dir err");
    const double rates[] = { 1000, 2000, 4000, 8000 };
    const double speeds[] = { 0.5, 2, 8, 32, 128 };  // counts/ms; 1600 DPI: 1 count/ms = 1.6 cm/s
    for (double hz : rates) {
        for (double v : speeds) {
            double dt = 1000.0 / hz;   // ms per packet
            double se = 0, maxdir = 0;
            int strokes = 0;
            for (int k = 0; k < 360; k++) {
                double th = 2 * PI * (k + 0.37) / 360;
                double cx = 0, cy = 0, ix = 0, iy = 0, ox = 0, oy = 0;
                int packets = int(300 / dt);
                for (int s = 0; s < packets; s++) {
                    cx += v * dt * std::cos(th);
                    cy += v * dt * std::sin(th);
                    double px = std::trunc(cx), py = std::trunc(cy);
                    cx -= px; cy -= py;
                    if (px == 0 && py == 0) continue;
                    vec2d o = r.run(px, py, 1, dt);
                    ix += px; iy += py; ox += o.x; oy += o.y;
                }
                double in_mag = std::hypot(ix, iy);
                if (in_mag == 0) continue;
                double eff = std::hypot(ox, oy) / in_mag;
                double ideal = smooth_target(std::atan2(iy, ix));
                double e = eff / ideal - 1;
                se += e * e;
                double turn = std::fabs(std::atan2(ix * oy - iy * ox, ix * ox + iy * oy));
                maxdir = std::fmax(maxdir, turn);
                strokes++;
            }
            std::printf("  %-8.0f %-12.1f %-14.3f %-14.4f%% %-10.3f deg\n", hz, v, v * dt,
                        100 * std::sqrt(se / strokes), maxdir * 180 / PI);
        }
    }
    std::printf("  (target RMS deviation from 1.0 is ~7%%; errors are relative to that target)\n");
}

static void report_layout()
{
    std::printf("[INFO struct sizes on this host (Windows differs: wchar_t is 2 bytes there)]\n");
    std::printf("  sizeof(angular_gain_args) = %zu\n", sizeof(ra::angular_gain_args));
    std::printf("  sizeof(profile)            = %zu\n", sizeof(ra::profile));
    std::printf("  sizeof(modifier_settings)  = %zu\n", sizeof(ra::modifier_settings));
}

int main()
{
    test_disabled_is_identity();
    test_grid_nodes_exact();
    test_linear_interp_and_wrap();
    test_dense_grid_matches_target();
    test_speed_axis();
    test_scalar_after_pipeline();
    test_stateless();
    test_validation();
    test_unvalidated_garbage_is_safe();
    report_layout();
    report_quantisation();

    std::printf("\n%d checks, %d failed\n", g_checks, g_failed);
    return g_failed == 0 ? 0 : 1;
}
