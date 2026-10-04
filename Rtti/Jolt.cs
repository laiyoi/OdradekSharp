using BinaryReader = OdradekSharp.Io.BinaryReader;

namespace OdradekSharp.Rtti;

/// <summary>
/// Port of odradek's Jolt physics binary reader, used by the PhysicsShapeResource callback
/// (middleware/jolt/**, callbacks/PhysicsShapeResourceCallback.java). The shapes themselves are
/// deserialized and thrown away by odradek, so this port only consumes the exact byte layout —
/// which is all that is needed to keep the object stream in sync.
/// </summary>
internal static class Jolt
{
    /// <summary>ShapeSubType.java — the order defines the on-disk tag byte.</summary>
    private enum ShapeSubType
    {
        Sphere, Box, Triangle, Capsule, TaperedCapsule, Cylinder, ConvexHull,
        StaticCompound, MutableCompound,
        RotatedTranslated, Scaled, OffsetCenterOfMass,
        Mesh, HeightField, SoftBody,
        User1, User2, User3, User4, User5, User6, User7, User8,
        UserConvex1, UserConvex2, UserConvex3, UserConvex4, UserConvex5, UserConvex6, UserConvex7, UserConvex8,
        Plane, TaperedCylinder, Empty,
    }

    /// <summary>Port of PhysicsShapeResourceCallback.deserialize.</summary>
    public static void ReadPhysicsShapeResource(BinaryReader r)
    {
        // The shape map only tracks *how many* shapes were read before (odradek keeps the objects, but
        // they are discarded; only the count feeds the "already deserialized" short-circuit).
        RestoreShape(r, []);
    }

    /// <summary>Port of PhysicsShapeResourceCallback.restoreFromBinaryState.</summary>
    private static void RestoreShape(BinaryReader r, List<int> shapeMap)
    {
        var shapeId = r.ReadInt();
        if (shapeId >= 0 && shapeId < shapeMap.Count) return; // already deserialized

        ReadShape(r);
        // odradek pads the map when shapeId skips ids, then asserts shapeId == size
        while (shapeMap.Count < shapeId) shapeMap.Add(shapeMap.Count);
        shapeMap.Add(shapeId);

        var childCount = r.ReadInt();
        for (var i = 0; i < childCount; i++) RestoreShape(r, shapeMap);

        var materialCount = r.ReadInt();
        for (var i = 0; i < materialCount; i++) r.ReadInt(); // -1 = null; materials are ignored
    }

    /// <summary>Port of Shape.sRestoreFromBinaryState + ShapeFunctions + each shape's restoreBinaryState.</summary>
    private static void ReadShape(BinaryReader r)
    {
        var subType = (ShapeSubType)r.ReadByte();
        switch (subType)
        {
            // ConvexShape: long userData, float density
            case ShapeSubType.Sphere:            { Convex(r); r.Skip(4); break; }                      // radius
            case ShapeSubType.Box:               { Convex(r); Vec3(r); r.Skip(4); break; }             // halfExtent, convexRadius
            case ShapeSubType.Capsule:           { Convex(r); r.Skip(8); break; }                      // radius, halfHeightOfCylinder
            case ShapeSubType.TaperedCapsule:    { Convex(r); Vec3(r); r.Skip(7 * 4); break; }         // centerOfMass + 7 floats
            case ShapeSubType.Cylinder:          { Convex(r); r.Skip(3 * 4); break; }                  // halfHeight, radius, convexRadius
            case ShapeSubType.ConvexHull:        ReadConvexHull(r); break;
            case ShapeSubType.StaticCompound:    ReadStaticCompound(r); break;

            // Non-convex / decorated shapes: only the base userData is read
            case ShapeSubType.Mesh:              { UserData(r); ReadBytes(r); break; }                 // tree
            case ShapeSubType.HeightField:       ReadHeightField(r); break;
            case ShapeSubType.RotatedTranslated: { UserData(r); Vec3(r); r.Skip(4 * 4); break; }       // centerOfMass + quaternion
            case ShapeSubType.Scaled:            { UserData(r); Vec3(r); break; }                      // scale
            case ShapeSubType.OffsetCenterOfMass:{ UserData(r); Vec3(r); break; }                      // offset

            // ShapeFunctions.get() throws for these (not registered in odradek either)
            default:
                throw new NotSupportedException($"Unknown Jolt shape subtype: {subType}");
        }
    }

    private static void UserData(BinaryReader r) => r.ReadLong();
    private static void Convex(BinaryReader r) { UserData(r); r.ReadFloat(); } // + density

    /// <summary>ConvexHullShape.restoreBinaryState.</summary>
    private static void ReadConvexHull(BinaryReader r)
    {
        Convex(r);
        Vec3(r);                    // centerOfMass
        r.Skip(16 * 4);             // inertia (Mat44)
        r.Skip(6 * 4);              // localBounds (AABox = 2 × Vec3)
        var points = ReadCount(r);
        for (var i = 0; i < points; i++)
        {
            AlignedVec3(r);         // position
            r.ReadInt();            // facesCount
            r.Skip(3 * 4);          // faces (always 3 ints)
        }
        var faces = ReadCount(r);
        for (var i = 0; i < faces; i++) r.Skip(2 * 2);   // firstVertex, vertexCount
        var planes = ReadCount(r);
        for (var i = 0; i < planes; i++) { Vec3(r); r.ReadFloat(); }
        ReadBytes(r);               // vertexIdx
        r.Skip(3 * 4);              // convexRadius, volume, innerRadius
    }

    /// <summary>CompoundShape.restoreBinaryState + StaticCompoundShape.restoreBinaryState.</summary>
    private static void ReadStaticCompound(BinaryReader r)
    {
        UserData(r);
        Vec3(r);                    // centerOfMass
        r.Skip(6 * 4);              // localBounds
        r.ReadFloat();              // innerRadius
        var subShapes = ReadCount(r);
        for (var i = 0; i < subShapes; i++) { r.ReadInt(); Vec3(r); Vec3(r); } // userData, position, rotation
        var nodes = ReadCount(r);
        for (var i = 0; i < nodes; i++) r.Skip(6 * 4 * 2 + 4 * 4);             // 6 × 4 shorts + 4 ints
    }

    /// <summary>HeightFieldShape.restoreBinaryState.</summary>
    private static void ReadHeightField(BinaryReader r)
    {
        UserData(r);
        Vec3(r);                    // offset
        Vec3(r);                    // scale
        r.ReadInt();                // sampleCount
        r.ReadInt();                // blockSize
        r.ReadByte();               // bitsPerSample
        r.ReadShort();              // minSample
        r.ReadShort();              // maxSample
        var rangeBlocks = ReadCount(r);
        for (var i = 0; i < rangeBlocks; i++) r.Skip(4 * 2 * 2); // min[4] + max[4] shorts
        ReadBytes(r);               // heightSamples
        ReadBytes(r);               // activeEdges
        ReadBytes(r);               // materialIndices
        r.ReadInt();                // numBitsPerMaterialIndex
    }

    // JoltUtils helpers: counts are int64, vectors are 3 floats (aligned variant has a 4th copy of z)
    private static int ReadCount(BinaryReader r) => checked((int)r.ReadLong());
    private static void Vec3(BinaryReader r) => r.Skip(3 * 4);
    private static void AlignedVec3(BinaryReader r)
    {
        r.Skip(3 * 4);
        var w = r.ReadFloat();
        _ = w; // odradek asserts z == w
    }
    private static void ReadBytes(BinaryReader r) { var n = ReadCount(r); r.Skip(n); }

    // ---- ragdoll (PhysicsRagdollResourceCallback -> RagdollSettings.sRestoreFromBinaryState) -----
    //
    // Everything below is deserialized and thrown away by odradek as well; the shape reader above is
    // reused verbatim, because a body's shape is serialized with the very same
    // `Shape.sRestoreFromBinaryState` layout that the PhysicsShapeResource callback uses.
    //
    // Layout verified byte-for-byte on group 29371 object [89] (skeleton of 5 joints, 5 parts).

    /// <summary>Port of PhysicsRagdollResourceCallback.deserialize.</summary>
    public static void ReadPhysicsRagdollResource(BinaryReader r) => RestoreRagdollSettings(r);

    /// <summary>Port of RagdollSettings.sRestoreFromBinaryState.</summary>
    private static void RestoreRagdollSettings(BinaryReader r)
    {
        // odradek passes these down as `new ArrayList<>(capacity)` — they start EMPTY. (That is *not*
        // the same as the PhysicsShapeResource callback, whose shape map starts with a null sentinel.)
        var shapeMap = new List<int>();
        var groupFilterMap = new List<int>();

        ReadSkeleton(r);

        var parts = r.ReadInt();
        for (var i = 0; i < parts; i++)
        {
            RestoreBodyWithChildren(r, shapeMap, groupFilterMap);

            if (r.ReadBool()) // hasConstraint
                RestoreConstraintSettings(r);
            // NOTE: TwoBodyConstraintSettings is kept by odradek; only its bytes matter here.
        }
    }

    /// <summary>Port of Skeleton.restoreFromBinaryState (skeleton/Skeleton.java:14-20).</summary>
    private static void ReadSkeleton(BinaryReader r)
    {
        var joints = r.ReadInt();
        for (var i = 0; i < joints; i++)
        {
            ReadJoltString(r);      // name
            r.ReadInt();            // parentJointIndex
            ReadJoltString(r);      // parentName
        }
    }

    /// <summary>JoltUtils.readString: int64 length + that many UTF-8 bytes (JoltUtils.java:79-85).</summary>
    private static void ReadJoltString(BinaryReader r) => r.Skip(ReadCount(r));

    /// <summary>Port of BodyCreationSettings.sRestoreWithChildren (body/BodyCreationSettings.java:44-70).</summary>
    private static void RestoreBodyWithChildren(BinaryReader r, List<int> shapeMap, List<int> groupFilterMap)
    {
        // BodyCreationSettings.restoreBinaryState
        r.Skip(3 * 4);      // position
        r.Skip(4 * 4);      // rotation
        r.Skip(3 * 4);      // linearVelocity
        r.Skip(3 * 4);      // angularVelocity
        r.Skip(2 * 4);      // CollisionGroup.restoreFromBinaryState: groupId, subGroupId
        r.ReadShort();      // objectLayer
        r.ReadByte();       // motionType
        r.ReadBool();       // allowDynamicOrKinematic
        r.ReadByte();       // motionQuality
        r.ReadBool();       // allowSleeping
        r.Skip(7 * 4);      // friction, restitution, linearDamping, angularDamping,
                            // maxLinearVelocity, maxAngularVelocity, gravityFactor
        r.ReadByte();       // overrideMassProperties
        r.ReadFloat();      // inertiaMultiplier
        r.ReadFloat();      // MassProperties.restoreFromBinaryState: mass
        r.Skip(16 * 4);     //                                  inertia (Mat44)

        RestoreShapeWithChildren(r, shapeMap);

        var groupFilterId = r.ReadInt();
        if (groupFilterId == -1) return;

        if (groupFilterId >= groupFilterMap.Count)
        {
            // odradek asserts groupFilterId == groupFilterMap.size() here. The map is never actually
            // looked up (nothing is stored), only its size decides whether bytes follow.
            RestoreGroupFilter(r);
            groupFilterMap.Add(groupFilterId);
        }
    }

    /// <summary>
    /// Port of Shape.sRestoreWithChildren (shape/Shape.java:22-48).
    ///
    /// odradek reads the shape and its sub shapes and then throws NotImplementedException
    /// unconditionally (Shape.java:47). That line is dead for this data: every body shape id here is
    /// -1, which returns at the first check (verified on group 29371:89, and on the other two
    /// PhysicsRagdollResource sites). The bytes consumed are identical either way, so this returns
    /// normally instead of deliberately desynchronizing the rest of the group.
    /// </summary>
    private static void RestoreShapeWithChildren(BinaryReader r, List<int> shapeMap)
    {
        var shapeId = r.ReadInt();

        if (shapeId == -1) return;              // nullptr shape
        if (shapeId < shapeMap.Count) return;   // already deserialized (only the id space is tracked)

        ReadShape(r);
        shapeMap.Add(shapeId);

        var subShapes = r.ReadInt();
        for (var i = 0; i < subShapes; i++) RestoreShapeWithChildren(r, shapeMap);
    }

    /// <summary>Port of GroupFilter.sRestoreFromBinaryState + GroupFilterTable.restoreBinaryState.</summary>
    private static void RestoreGroupFilter(BinaryReader r)
    {
        var name = FactoryTypeName(r.ReadInt());
        if (name != "GroupFilterTable")
            throw new NotSupportedException($"Jolt group filter '{name}' is not implemented");

        r.ReadInt();        // numSubGroups
        ReadBytes(r);       // table (int64 length + bytes)
    }

    /// <summary>Port of ConstraintSettings.restoreFromBinaryState and its concrete subtypes.</summary>
    private static void RestoreConstraintSettings(BinaryReader r)
    {
        var name = FactoryTypeName(r.ReadInt());

        // The base fields are read first for every subtype: each override starts with super.restoreBinaryState.
        r.ReadBool();       // enabled
        r.ReadFloat();      // drawConstraintSize
        r.ReadInt();        // constraintPriority
        r.ReadInt();        // numVelocityStepsOverride
        r.ReadInt();        // numPositionStepsOverride

        switch (name)
        {
            case "HingeConstraintSettings":
                // TwoBodyConstraintSettings is empty, so the subtype fields follow immediately.
                r.Skip(6 * 3 * 4);  // point1, hingeAxis1, normalAxis1, point2, hingeAxis2, normalAxis2
                r.Skip(3 * 4);      // limitsMin, limitsMax, maxFrictionForce
                ReadMotorSettings(r);   // motorSettings
                break;

            case "PointConstraintSettings":
                r.Skip(3 * 4);      // commonPoint
                break;

            case "SwingTwistConstraintSettings":
                r.Skip(6 * 3 * 4);  // position1/2, twistAxis1/2, planeAxis1/2
                r.Skip(5 * 4);      // normalHalfConeAngle, planeHalfConeAngle, twistMinAngle,
                                    // twistMaxAngle, maxFrictionTorque
                ReadMotorSettings(r);   // swingMotorSettings
                ReadMotorSettings(r);   // twistMotorSettings
                break;

            case "SliderConstraintSettings":
                r.Skip(3 * 4);      // sliderAxis
                r.Skip(3 * 4);      // limitsMin, limitsMax, maxFrictionForce
                ReadMotorSettings(r);   // swingMotorSettings
                break;

            default:
                // ConstraintSettings.restoreFromBinaryState throws UnsupportedOperationException for
                // every other name in the factory table.
                throw new NotSupportedException($"Jolt constraint {name} is not implemented");
        }
    }

    /// <summary>MotorSettings.restoreBinaryState: 6 floats.</summary>
    private static void ReadMotorSettings(BinaryReader r) => r.Skip(6 * 4);

    // ---- Jolt type factory (core/Factory.java) ---------------------------------------------------

    /// <summary>
    /// Port of Jolt's Factory.getTypeName: the 32-bit hash of a type name is
    /// FNV-1a 64 over the UTF-8 name, folded with `hash ^ (hash >>> 32)` (Factory.java:159-163).
    ///
    /// The convention was confirmed against the shipped assets rather than guessed: the group filter
    /// hash read for group 29371:89 is 0x4c5c6618, which is exactly this function applied to
    /// "GroupFilterTable" (the byte-swapped and UTF-16 variants do not match).
    /// </summary>
    private static string FactoryTypeName(int hash) =>
        FactoryTypeNames.TryGetValue(hash, out var name)
            ? name
            : throw new InvalidDataException($"Unknown Jolt type hash: 0x{hash:x8}");

    /// <summary>Factory.TYPE_NAMES, in the order odradek declares them.</summary>
    private static readonly string[] FactoryNames =
    [
        "SkeletalAnimation",
        "Skeleton",
        "CompoundShapeSettings",
        "StaticCompoundShapeSettings",
        "MutableCompoundShapeSettings",
        "TriangleShapeSettings",
        "SphereShapeSettings",
        "BoxShapeSettings",
        "CapsuleShapeSettings",
        "TaperedCapsuleShapeSettings",
        "CylinderShapeSettings",
        "ScaledShapeSettings",
        "MeshShapeSettings",
        "ConvexHullShapeSettings",
        "HeightFieldShapeSettings",
        "RotatedTranslatedShapeSettings",
        "OffsetCenterOfMassShapeSettings",
        "RagdollSettings",
        "PointConstraintSettings",
        "SixDOFConstraintSettings",
        "SliderConstraintSettings",
        "SwingTwistConstraintSettings",
        "DistanceConstraintSettings",
        "HingeConstraintSettings",
        "FixedConstraintSettings",
        "ConeConstraintSettings",
        "PathConstraintSettings",
        "VehicleConstraintSettings",
        "WheeledVehicleControllerSettings",
        "PathConstraintPath",
        "PathConstraintPathHermite",
        "MotorSettings",
        "PhysicsScene",
        "PhysicsMaterial",
        "PhysicsMaterialSimple",
        "GroupFilter",
        "GroupFilterTable",
    ];

    private static readonly Dictionary<int, string> FactoryTypeNames = BuildFactoryTypeNames();

    private static Dictionary<int, string> BuildFactoryTypeNames()
    {
        var map = new Dictionary<int, string>(FactoryNames.Length);
        foreach (var name in FactoryNames)
        {
            var hash = Fnv1a64Fold(name);
            // A collision would make getTypeName ambiguous; odradek's map silently overwrites.
            if (!map.TryAdd(hash, name))
                throw new InvalidDataException($"Jolt factory type hash collision for {name}");
        }
        return map;
    }

    private static int Fnv1a64Fold(string name)
    {
        var hash = 0xcbf29ce484222325UL;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(name))
        {
            hash ^= b;
            hash *= 0x100000001b3UL;
        }
        return unchecked((int)(hash ^ (hash >> 32)));
    }
}
