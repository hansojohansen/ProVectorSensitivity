// OpenVector RESEARCH build - equivalence check.
//
// Built twice by run.sh: once against pristine upstream Raw Accel 1.7.1 common/ headers
// (reconstructed by reverse-applying patches/*.patch) and once against the research headers.
// It touches only upstream profile fields, so with the angular table off both binaries must
// print bit-identical outputs for the same random settings and packet streams (including the
// stateful smoothers and every accel mode).

#include "rawaccel.hpp"
#include "rawaccel-validate.hpp"

#include <cstdio>
#include <random>

namespace ra = rawaccel;

int main()
{
    std::mt19937_64 rng(20261025);
    std::uniform_real_distribution<double> U(0, 1);
    const ra::accel_mode modes[] = {
        ra::accel_mode::noaccel, ra::accel_mode::classic, ra::accel_mode::jump, ra::accel_mode::natural,
        ra::accel_mode::synchronous, ra::accel_mode::power, ra::accel_mode::lookup,
    };
    int printed = 0, skipped = 0;

    for (int trial = 0; trial < 300; trial++) {
        ra::modifier_settings s;
        auto& p = s.prof;
        p.output_dpi = 200 + 3000 * U(rng);
        p.yx_output_dpi_ratio = 0.5 + U(rng);
        p.lr_output_dpi_ratio = 0.5 + U(rng);
        p.ud_output_dpi_ratio = 0.5 + U(rng);
        p.degrees_rotation = (U(rng) < 0.4) ? -30 + 60 * U(rng) : 0;
        p.degrees_snap = (U(rng) < 0.4) ? 45 * U(rng) : 0;
        p.range_weights = { 2 * U(rng), 2 * U(rng) };
        p.domain_weights = { 0.2 + 2 * U(rng), 0.2 + 2 * U(rng) };
        p.speed_processor_args.whole = U(rng) < 0.6;
        p.speed_processor_args.lp_norm = (U(rng) < 0.5) ? 2 : 0.5 + 20 * U(rng);
        p.speed_processor_args.input_speed_smooth_halflife = (U(rng) < 0.3) ? 10 * U(rng) : 0;
        p.speed_processor_args.scale_smooth_halflife = (U(rng) < 0.3) ? 10 * U(rng) : 0;
        p.speed_processor_args.output_speed_smooth_halflife = (U(rng) < 0.3) ? 10 * U(rng) : 0;
        p.speed_max = (U(rng) < 0.2) ? 50 * U(rng) : 0;

        for (ra::accel_args* a : { &p.accel_x, &p.accel_y }) {
            a->mode = modes[int(U(rng) * 7) % 7];
            a->gain = U(rng) < 0.5;
            a->acceleration = 0.001 + 0.05 * U(rng);
            a->exponent_classic = 1.5 + U(rng);
            a->exponent_power = 0.05 + 0.5 * U(rng);
            a->limit = 1.1 + U(rng);
            a->motivity = 1.1 + U(rng);
            a->sync_speed = 1 + 10 * U(rng);
            a->cap = { 5 + 20 * U(rng), 1.2 + U(rng) };
            if (a->mode == ra::accel_mode::lookup) {
                int pts = 8;
                a->length = 2 * pts;
                for (int i = 0; i < pts; i++) {
                    a->data[2 * i] = float(1 + i * 5);
                    a->data[2 * i + 1] = float((a->gain ? (1 + i * 5) : 1) * (1 + 0.1 * i));
                }
            }
        }

        if (ra::valid(p).count != 0) { skipped++; continue; }

        ra::init_data(s);
        ra::modifier mod(s);
        ra::speed_processor sp;
        sp.init(p.speed_processor_args);

        for (int k = 0; k < 200; k++) {
            vec2d v = { double(int(U(rng) * 255) - 127), double(int(U(rng) * 255) - 127) };
            if (v.x == 0 && v.y == 0) continue;
            double dpi_factor = (U(rng) < 0.5) ? 1 : 1000.0 / (400 + 3200 * U(rng));
            double time = 0.125 + 4 * U(rng);
            mod.modify(v, sp, s, dpi_factor, time);
            std::printf("%d %d %.17g %.17g\n", trial, k, v.x, v.y);
            printed++;
        }
    }
    std::fprintf(stderr, "equiv: %d outputs, %d invalid random profiles skipped\n", printed, skipped);
    return 0;
}
