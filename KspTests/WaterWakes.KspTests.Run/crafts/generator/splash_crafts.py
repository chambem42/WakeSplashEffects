"""Makes WaterWakes' splash test crafts (SplashTests): a ProceduralParts Ore tank each (its ore, which the test sets,
makes its mass), from template.craft (FAR's Planing Box.craft), VAB crafts standing as they are to fall, written beside
this folder (crafts/):

    Splash Cylinder.craft        a cylinder 2.5 m across and 4 m long standing on its end (flat end down); 19.6 m3
                                 (Splash_SideCylinderDrops turns it on its side before the drop)
    Splash Sphere.craft          a 2.5 m ball (a CustomProceduralParts Spine of round sections); 8.2 m3
    Splash Wedge.craft           a triangular prism lying with an edge down (equilateral, 2.5 m sides: 30 deg deadrise),
                                 1.96 m long; 5.3 m3
    Splash Submarine.craft       DARPA SUBOFF's bare hull (Groves et al.'s equations, as FAR's crafts/suboff.py) at a real
                                 submarine's size, 10 m across and 85.8 m long, lying level, nose to craft +x

The upright three share a plan area (what faces the water from above), 4.91 m2, a 2.5 m disc's. Splash Apollo Pod.craft
(the stock Mk1-3 pod at Apollo's size, mods/WaterWakesTests/ApolloPod.cfg) is made by hand.

    python splash_crafts.py

Craft frame: x right, y up, z forward; a ProceduralParts shape's axis is its part's y."""
import math
import os
import re

here = os.path.dirname(os.path.abspath(__file__))
out = os.path.dirname(here)
END = "\t\tEVENTS\n\t\t{\n\t\t}\n\t\tACTIONS\n\t\t{\n\t\t}\n\t\tUPGRADESAPPLIED\n\t\t{\n\t\t}\n\t}\n"


def cylinder(d, l):
    return "Cylinder", "\tMODULE\n\t{\n\t\tname = ProceduralShapeCylinder\n\t\tisEnabled = True\n\t\tdiameter = %g\n\t\tlength = %g\n\t\tstagingEnabled = True\n" % (d, l) + END


def polygon(corners, d, l):
    # (an odd polygon's diameter is its height, corner to the opposite side)
    return "Polygon", ("\tMODULE\n\t{\n\t\tname = ProceduralShapePolygon\n\t\tisEnabled = True\n\t\tcornerCount = %d\n\t\tdiameter = %g\n\t\tlength = %g\n"
                       "\t\tstagingEnabled = True\n" % (corners, d, l)) + END


def sphere(d, n=13):
    nodes = ""
    for i in range(n):
        p = i / (n - 1)
        size = max(0.08, d * math.sqrt(max(0.0, 1 - (2 * p - 1) ** 2)))
        nodes += "\t\tSPINE_NODE\n\t\t{\n\t\t\tposition = %g\n\t\t\tsizeH = %.4f\n\t\t\tsizeV = %.4f\n\t\t\tsection = Ellipse\n\t\t}\n" % (p, size, size)
    return "Spine", ("\tMODULE\n\t{\n\t\tname = ProceduralShapeSpine\n\t\tisEnabled = True\n\t\tlength = %g\n\t\thScale = 1\n\t\tvScale = 1\n"
                     "\t\tstagingEnabled = True\n" % d) + nodes + END


def spine(length, nodes):
    """a CustomProceduralParts Spine of round sections: nodes (position 0-1 from the part's -y end, diameter m)"""
    body = ""
    for pos, d in nodes:
        body += "\t\tSPINE_NODE\n\t\t{\n\t\t\tposition = %g\n\t\t\tsizeH = %.4f\n\t\t\tsizeV = %.4f\n\t\t\tsection = Ellipse\n\t\t}\n" % (pos, d, d)
    return "Spine", ("\tMODULE\n\t{\n\t\tname = ProceduralShapeSpine\n\t\tisEnabled = True\n\t\tlength = %g\n\t\thScale = 1\n\t\tvScale = 1\n"
                     "\t\tstagingEnabled = True\n" % length) + body + END


SUB_D = 10.0


# DARPA SUBOFF's bare hull (Groves et al. Table 1, as FAR's crafts/suboff.py): its radius (ft) along it from the nose
FT = 0.3048
R_MAX = 5.0 / 6.0                 # ft
X_BOW, X_AFT, X_AP, X_END = 3.333333, 10.645833, 13.979167, 14.291667   # ft from the nose


def radius_ft(x):
    if x <= 0:
        return 0.0
    if x < X_BOW:
        a = 0.3 * x - 1
        v = 1.126395101 * x * a ** 4 + 0.442874707 * x * x * a ** 3 + 1 - a ** 4 * (1.2 * x + 1)
        return R_MAX * max(v, 0) ** (1 / 2.1)
    if x <= X_AFT:
        return R_MAX
    if x <= X_AP:
        rh, k0, k1 = 0.1175, 10.0, 44.6244
        xi = (X_AP - x) / 3.333333
        v = (rh * rh + rh * k0 * xi ** 2 + (20 - 20 * rh * rh - 4 * rh * k0 - k1 / 3) * xi ** 3 +
             (-45 + 45 * rh * rh + 6 * rh * k0 + k1) * xi ** 4 + (36 - 36 * rh * rh - 4 * rh * k0 - k1) * xi ** 5 +
             (-10 + 10 * rh * rh + rh * k0 + k1 / 3) * xi ** 6)
        return R_MAX * max(v, 0) ** 0.5
    if x < X_END:
        return 0.1175 * R_MAX * max(0.0, 1 - (3.2 * x - 44.733333) ** 2) ** 0.5
    return 0.0


def stations():
    # x (ft) from the nose: cosine-spaced on the bow and the afterbody, the cap's points, the middle body's ends
    bow = [X_BOW * (1 - math.cos(math.pi / 2 * i / 14)) for i in range(15)]
    aft = [X_AFT + (X_AP - X_AFT) * (1 - math.cos(math.pi * i / 14)) / 2 for i in range(15)]
    cap = [X_AP + (X_END - X_AP) * f for f in (0.4, 0.7, 0.9, 1.0)]
    return sorted(set(round(x, 6) for x in bow + aft + cap))


def submarine_nodes():
    s = SUB_D / (2 * R_MAX * FT)
    nodes = []
    for x in reversed(stations()):      # tail (position 0) to nose (1)
        nodes.append((round(1 - x / X_END, 6), round(max(0.05, 2 * radius_ft(x) * FT * s), 4)))
    return X_END * FT * s, nodes


def make(ship, what, shape, rot, size, height):
    src = open(os.path.join(here, "template.craft"), encoding="utf-8-sig").read()
    src = src.replace("ship = Planing Box", "ship = " + ship)
    src = re.sub(r"(?m)^description = .*$", lambda m: "description = " + what, src)
    src = re.sub(r"(?m)^type = .*$", "type = VAB", src)
    src = re.sub(r"(?m)^size = .*$", "size = %g,%g,%g" % size, src)
    src = src.replace("part = proceduralTankLiquid_", "part = proceduralTankOre_")
    src = re.sub(r"(?m)^\tpos = .*$", "\tpos = 0,%g,0" % (height / 2 + 1), src, count=1)
    src = re.sub(r"(?m)^\tattPos0 = .*$", "\tattPos0 = 0,%g,0" % (height / 2 + 1), src, count=1)
    src = re.sub(r"(?m)^\trot = .*$", "\trot = " + rot, src, count=1)
    src = re.sub(r"(?m)^\tattRot0 = .*$", "\tattRot0 = " + rot, src, count=1)
    name, module = shape
    src = src.replace("shapeName = Polygon", "shapeName = " + name)
    start = src.index("\tMODULE\n\t{\n\t\tname = ProceduralShapePolygon")
    end = src.index("\tMODULE\n\t{\n\t\tname = TankContentSwitcher")
    src = src[:start] + module + src[end:]
    src = re.sub(r"(?m)^(\t\ttankType = ).*$", r"\1Ore", src)
    return ship + ".craft", src


UPRIGHT = "0,0,0,1"
# the part's y along the craft's x (lying), its z up: the triangle's corner at local -z (ProceduralShapePolygon's
# corners from 30 deg: 30, 150, 270) points down
WEDGE = "-0.5,-0.5,-0.5,0.5"
# the part's y along the craft's x (lying)
SIDE = "0,0,-0.707106781,0.707106781"

CRAFTS = [
    make("Splash Cylinder", "A plain cylinder for WaterWakes' splash tests: one ProceduralParts Ore tank, 2.5 m across, 4 m long, standing on its end",
         cylinder(2.5, 4.0), UPRIGHT, (2.5, 4.0, 2.5), 4.0),
    make("Splash Sphere", "A 2.5 m ball for WaterWakes' splash tests: one ProceduralParts Ore tank, a Spine of round sections",
         sphere(2.5), UPRIGHT, (2.5, 2.5, 2.5), 2.5),
    make("Splash Wedge", "A wedge for WaterWakes' splash tests: one ProceduralParts Ore tank, a triangular prism (2.5 m sides) 1.96 m long, lying edge down",
         polygon(3, 2.5 * math.sqrt(3) / 2, 1.96), WEDGE, (1.96, 2.165, 2.5), 2.165),
    make("Splash Submarine", "DARPA SUBOFF's bare hull at a real submarine's size (10 m across, 85.8 m long) for WaterWakes' splash tests: one "
         "ProceduralParts Ore tank, a Spine, lying level", spine(*submarine_nodes()), SIDE, (submarine_nodes()[0], SUB_D, SUB_D), SUB_D),
]

if __name__ == "__main__":
    print("submarine %.2f m long, %d nodes" % (submarine_nodes()[0], len(submarine_nodes()[1])))
    for name, text in CRAFTS:
        with open(os.path.join(out, name), "w", encoding="utf-8", newline="\n") as f:
            f.write(text)
        print("wrote", name)
