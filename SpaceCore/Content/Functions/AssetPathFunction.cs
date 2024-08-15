using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SpaceCore.Content.Functions;
internal class AssetPathFunction : BaseFunction
{
    public bool AbsolutePaths { get; }

    public AssetPathFunction(bool absolute)
    :   base( absolute ? "@@" : "@" )
    {
        AbsolutePaths = absolute;
    }

    public override SourceElement Simplify(FuncCall fcall, ContentEngine ce)
    {
        if (fcall.Parameters.Count != 1)
            throw new ArgumentException($"Asset path function @ must have exactly one string parameter, at {fcall.FilePath}:{fcall.Line}:{fcall.Column}");

        string path = Path.Combine(AbsolutePaths ? ce.ContentRootFolderActual : ce.ContentRootFolder, Path.GetDirectoryName(fcall.Parameters[0].FilePath), fcall.Parameters[0].SimplifyToToken(ce).Value).Replace('\\', '/');
        List<string> pathParts = new(path.Split('/'));
        for (int i = 1; i < pathParts.Count; ++i)
        {
            if (pathParts[i] == "..")
            {
                pathParts.RemoveAt(i);
                pathParts.RemoveAt(i - 1);
            }
        }
        path = string.Join('/', pathParts);

        return new Token()
        {
            FilePath = fcall.FilePath,
            Line = fcall.Line,
            Column = fcall.Column,
            Value = AbsolutePaths ? path : ce.Helper.ModContent.GetInternalAssetName(path).Name,
            IsString = true,
            Context = fcall.Context,
            Uid = fcall.Uid,
        };
    }
}