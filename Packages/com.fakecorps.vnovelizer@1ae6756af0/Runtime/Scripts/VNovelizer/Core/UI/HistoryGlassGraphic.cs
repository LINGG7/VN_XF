using UnityEngine;
using UnityEngine.UI;

/// <summary>Resolution-independent glass card and timeline marker for dialogue history.</summary>
[RequireComponent(typeof(CanvasRenderer))]
[AddComponentMenu("VNovelizer/UI/History Glass Graphic")]
public sealed class HistoryGlassGraphic : MaskableGraphic
{
    public enum Shape { Dialogue, TimelineNode }
    public Shape shape;

    protected override void OnPopulateMesh(VertexHelper mesh)
    {
        mesh.Clear();
        Rect rect=GetPixelAdjustedRect();
        if(rect.width<=0 || rect.height<=0) return;
        if(shape==Shape.TimelineNode) { DrawNode(mesh,rect); return; }
        float cut=Mathf.Min(20f,rect.height*.15f);
        Vector2[] points={new Vector2(rect.xMin,rect.yMin),new Vector2(rect.xMin,rect.yMax-cut),new Vector2(rect.xMin+cut,rect.yMax),new Vector2(rect.xMax,rect.yMax),new Vector2(rect.xMax,rect.yMin+cut),new Vector2(rect.xMax-cut,rect.yMin)};
        Polygon(mesh,points,new Color(.76f,.86f,.96f,.94f),new Color(.88f,.94f,1f,.96f),rect);
        Color edge=new Color(.63f,.87f,1f,.9f);
        for(int i=0;i<points.Length;i++) Line(mesh,points[i],points[(i+1)%points.Length],1.4f,edge);
        // Shallow city-like facets echo the reference without baking any dialogue into a texture.
        float skylineWidth=Mathf.Min(250f,rect.width*.2f);
        for(int i=0;i<9;i++) {
            float x=rect.xMax-skylineWidth+i*skylineWidth/9f;
            float h=rect.height*(.2f+((i*7)%9)*.065f);
            Quad(mesh,new Rect(x,rect.yMin+8,skylineWidth/10f,h),new Color(.14f,.28f,.4f,.065f));
            Line(mesh,new Vector2(x+3,rect.yMin+h),new Vector2(x+10,rect.yMin+h+13),1f,new Color(.48f,.7f,.87f,.17f));
        }
        Line(mesh,new Vector2(rect.xMax-9,rect.yMax-8),new Vector2(rect.xMax-9,rect.yMin+cut+9),2f,new Color(.8f,.94f,1f,.8f));
        Line(mesh,new Vector2(rect.xMax-9,rect.yMin+cut+9),new Vector2(rect.xMax-cut-9,rect.yMin+9),2f,new Color(.8f,.94f,1f,.8f));
        Quad(mesh,new Rect(rect.xMin+18,rect.yMin+8,58,3),new Color(.4f,.74f,.96f,.55f));
        Quad(mesh,new Rect(rect.xMin+84,rect.yMin+8,17,3),new Color(.62f,.86f,1f,.65f));
        // Small left pointer aligns each card to its speaker nameplate.
        float mid=rect.yMax-58;
        Polygon(mesh,new[]{new Vector2(rect.xMin,mid-9),new Vector2(rect.xMin-16,mid),new Vector2(rect.xMin,mid+9)},new Color(.83f,.93f,1,.94f),new Color(.83f,.93f,1,.94f),rect);
    }
    static void DrawNode(VertexHelper mesh,Rect rect)
    {
        Vector2 c=rect.center;
        for(int pass=0;pass<3;pass++) {
            float radius=Mathf.Min(rect.width,rect.height)*(.49f-pass*.1f);
            float inner=radius*.25f;
            Vector2[] points={c+Vector2.left*radius,c+new Vector2(-inner,inner),c+Vector2.up*radius,c+new Vector2(inner,inner),c+Vector2.right*radius,c+new Vector2(inner,-inner),c+Vector2.down*radius,c+new Vector2(-inner,-inner)};
            Color shade=pass==0?new Color(.25f,.65f,1,.18f):pass==1?new Color(.45f,.8f,1,.6f):new Color(.9f,.98f,1,1);
            Polygon(mesh,points,shade,shade,rect);
        }
    }
    static void Polygon(VertexHelper mesh,Vector2[] p,Color bottom,Color top,Rect bounds)
    {
        int start=mesh.currentVertCount;
        Vector2 center=Vector2.zero; foreach(var v in p) center+=v; center/=p.Length;
        mesh.AddVert(center,Color.Lerp(bottom,top,Mathf.InverseLerp(bounds.yMin,bounds.yMax,center.y)),Vector2.zero);
        foreach(var v in p) mesh.AddVert(v,Color.Lerp(bottom,top,Mathf.InverseLerp(bounds.yMin,bounds.yMax,v.y)),Vector2.zero);
        for(int i=0;i<p.Length;i++) mesh.AddTriangle(start,start+1+i,start+1+(i+1)%p.Length);
    }
    static void Quad(VertexHelper mesh,Rect rect,Color color)
    {
        int start=mesh.currentVertCount;
        mesh.AddVert(new Vector2(rect.xMin,rect.yMin),color,Vector2.zero); mesh.AddVert(new Vector2(rect.xMin,rect.yMax),color,Vector2.zero);
        mesh.AddVert(new Vector2(rect.xMax,rect.yMax),color,Vector2.zero); mesh.AddVert(new Vector2(rect.xMax,rect.yMin),color,Vector2.zero);
        mesh.AddTriangle(start,start+1,start+2); mesh.AddTriangle(start,start+2,start+3);
    }
    static void Line(VertexHelper mesh,Vector2 a,Vector2 b,float width,Color color)
    {
        Vector2 n=new Vector2(-(b-a).y,(b-a).x).normalized*(width*.5f); int start=mesh.currentVertCount;
        mesh.AddVert(a-n,color,Vector2.zero); mesh.AddVert(a+n,color,Vector2.zero); mesh.AddVert(b+n,color,Vector2.zero); mesh.AddVert(b-n,color,Vector2.zero);
        mesh.AddTriangle(start,start+1,start+2); mesh.AddTriangle(start,start+2,start+3);
    }
}