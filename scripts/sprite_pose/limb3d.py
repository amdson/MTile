"""Ball-and-hinge limb kinematics, projected to the side view.

A limb chain is (upper, lower) — shoulder/elbow or hip/knee. Its true pose:
  theta  in-plane azimuth of the upper bone, local to its parent (as the clip stores it)
  phi    out-of-plane tilt of the upper bone (+ toward the camera)
  alpha  hinge flexion of the lower bone (the clip's local rotation when the limb is planar,
         so the rig's joint limits apply to it unchanged)
  psi    swivel of the hinge plane about the upper bone (0 = bend axis faces the camera)

Projection gives what the clip can store: each bone's in-plane local Rotation and its
Stretch (projected length / true length = cos of the bone's own tilt), plus the depth
sign of each tip for a future front/behind draw-order flag. With phi = psi = 0 this is
exactly the planar 2D chain.

Why a hinge: a straight limb can only foreshorten by tilting its upper bone, and a forearm
only foreshortens if the elbow is really bent out of the screen plane — so a short-drawn
limb can't be "explained" by an anatomically impossible fold.
"""
import math
import numpy as np

Z = np.array([0.0, 0.0, 1.0])


def chain_dirs(parent_world, theta, phi, alpha, psi):
    """3D unit directions (upper, lower) in the screen frame (x right, y down, z toward camera)."""
    az = parent_world + theta
    u = np.array([math.cos(phi) * math.cos(az), math.cos(phi) * math.sin(az), math.sin(phi)])
    h0 = Z - u[2] * u                      # bend axis: the screen normal made perpendicular to u
    n = np.linalg.norm(h0)
    h0 = h0 / n if n > 1e-6 else np.array([-math.sin(az), math.cos(az), 0.0])
    b = np.cross(u, h0)
    h = h0 * math.cos(psi) + b * math.sin(psi)
    f = u * math.cos(alpha) + np.cross(h, u) * math.sin(alpha)
    return u, f


def project(parent_world, theta, phi, alpha, psi):
    """-> (upper_local, upper_stretch, lower_local, lower_stretch, upper_z, lower_z)."""
    u, f = chain_dirs(parent_world, theta, phi, alpha, psi)
    su = math.hypot(u[0], u[1])
    sf = math.hypot(f[0], f[1])
    wu = math.atan2(u[1], u[0])
    wf = math.atan2(f[1], f[0]) if sf > 1e-6 else wu
    wrap = lambda x: (x + math.pi) % (2 * math.pi) - math.pi
    return wrap(wu - parent_world), su, wrap(wf - wu), sf, float(u[2]), float(f[2])


if __name__ == '__main__':
    # planar reduction: phi = psi = 0 must reproduce the 2D chain exactly
    for th, al in [(0.3, -0.8), (1.2, 0.0), (-2.0, 1.5)]:
        r = project(0.4, th, 0.0, al, 0.0)
        assert abs(r[0] - th) < 1e-9 and abs(r[2] - al) < 1e-9 and abs(r[1] - 1) < 1e-9 and abs(r[3] - 1) < 1e-9, r
    # a straight limb tilted 60 deg: both bones foreshorten to cos 60 and stay collinear
    r = project(0.0, 1.0, math.radians(60), 0.0, 0.3)
    assert abs(r[1] - 0.5) < 1e-9 and abs(r[3] - 0.5) < 1e-9 and abs(r[2]) < 1e-9, r
    # planar upper, elbow bent 90 deg with the hinge swivelled 90 deg: forearm points at the camera
    r = project(0.0, 0.0, 0.0, -math.pi / 2, math.pi / 2)
    assert r[3] < 1e-9 and abs(abs(r[5]) - 1) < 1e-9, r
    # mirror: flipping phi and psi together mirrors depth only - identical projection
    a = project(0.2, 0.5, 0.6, -1.0, 0.4)
    b = project(0.2, 0.5, -0.6, -1.0, -0.4)
    assert all(abs(x - y) < 1e-9 for x, y in zip(a[:4], b[:4])) and abs(a[4] + b[4]) < 1e-9, (a, b)
    print('limb3d: all checks pass')
