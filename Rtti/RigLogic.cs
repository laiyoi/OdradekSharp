using BinaryReader = OdradekSharp.Io.BinaryReader;

namespace OdradekSharp.Rtti;

/// <summary>
/// Port of odradek's RigLogic binary reader, used by the FacialRigSettingWithLODResource callback
/// (middleware/riglogic/**, callbacks/FacialRigSettingWithLODResourceCallback.java).
///
/// The whole section is BIG_ENDIAN (RigLogic.java:56 restores LITTLE_ENDIAN in a finally block).
/// Nothing here is kept — odradek deserializes the rig and throws it away — so this only consumes the
/// exact byte layout, which is what keeps the object stream in sync.
///
/// Verified on the real data: group 1050 (previously truncated at object [3650], a
/// FacialRigSettingWithLODResource) now parses all 7,966 objects and the last one ends exactly on the
/// span boundary.
/// </summary>
internal static class RigLogic
{
    /// <summary>Port of RigLogic.read (RigLogic.java:55-69).</summary>
    public static void Read(BinaryReader r)
    {
        // Configuration.read
        _ = r.ReadIntBE();                  // calculationType (CalculationType ordinal)
        var loadJoints = r.ReadBool();      // BoolFormat.BYTE reads are order-independent
        var loadBlendShapes = r.ReadBool();
        var loadAnimatedMaps = r.ReadBool();
        _ = r.ReadBool();                   // loadMachineLearnedBehavior

        ReadRigMetrics(r);
        ReadControls(r);
        ReadJoints(r, loadJoints);
        if (loadBlendShapes) ReadBlendShapes(r);
        if (loadAnimatedMaps) ReadAnimatedMaps(r);
    }

    /// <summary>RigMetrics.read: 8 shorts.</summary>
    private static void ReadRigMetrics(BinaryReader r)
    {
        for (var i = 0; i < 8; i++) r.ReadShortBE();
    }

    /// <summary>Controls.read: guiToRawMapping, psds.</summary>
    private static void ReadControls(BinaryReader r)
    {
        ReadConditionalTable(r);
        ReadPsdMatrix(r);
    }

    /// <summary>ConditionalTable.read: 7 sized arrays then 2 shorts.</summary>
    private static void ReadConditionalTable(BinaryReader r)
    {
        ReadShorts(r);              // intervalsRemaining
        ReadShorts(r);              // inputIndices
        ReadShorts(r);              // outputIndices
        ReadFloats(r);              // fromValues
        ReadFloats(r);              // toValues
        ReadFloats(r);              // slopeValues
        ReadFloats(r);              // cutValues
        r.ReadShortBE();            // inputCount
        r.ReadShortBE();            // outputCount
    }

    /// <summary>PSDMatrix.read: short, then 2 sized short arrays and 1 sized float array.</summary>
    private static void ReadPsdMatrix(BinaryReader r)
    {
        r.ReadShortBE();            // distinctPSDs
        ReadShorts(r);              // rowIndices
        ReadShorts(r);              // columnIndices
        ReadFloats(r);              // values
    }

    /// <summary>Joints.read: optional evaluator, neutral values, variable attribute indices, group count.</summary>
    private static void ReadJoints(BinaryReader r, bool loadJoints)
    {
        if (loadJoints) ReadJointsEvaluator(r);

        ReadFloats(r);              // neutralValues

        var variableAttributeIndices = r.ReadIntBE();   // readObjects: int64 per element
        for (var i = 0; i < variableAttributeIndices; i++) ReadShorts(r);

        r.ReadShortBE();            // jointGroupCount
    }

    /// <summary>JointsEvaluator.read -> JointStorage.read.</summary>
    private static void ReadJointsEvaluator(BinaryReader r)
    {
        ReadShorts(r);              // values
        ReadShorts(r);              // inputIndices
        ReadShorts(r);              // outputIndices

        var lodRegions = r.ReadIntBE();                 // readObjects(LODRegion::read)
        for (var i = 0; i < lodRegions; i++) r.Skip(3 * 4);

        var jointGroups = r.ReadIntBE();                // readObjects(JointGroup::read)
        for (var i = 0; i < jointGroups; i++) r.Skip(8 * 4);
    }

    /// <summary>BlendShapes.read: 3 sized short arrays.</summary>
    private static void ReadBlendShapes(BinaryReader r)
    {
        ReadShorts(r);              // lods
        ReadShorts(r);              // inputIndices
        ReadShorts(r);              // outputIndices
    }

    /// <summary>AnimatedMaps.read: sized short array, then a ConditionalTable.</summary>
    private static void ReadAnimatedMaps(BinaryReader r)
    {
        ReadShorts(r);              // lods
        ReadConditionalTable(r);
    }

    // `reader.readShorts(reader.readInt())` / `readFloats(reader.readInt())`: the count is big-endian
    // as well, and Java evaluates it before the array read.
    private static void ReadShorts(BinaryReader r) => r.ReadShortsBE(r.ReadIntBE());
    private static void ReadFloats(BinaryReader r) => r.ReadFloatsBE(r.ReadIntBE());
}
