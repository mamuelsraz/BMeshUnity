/*
 * Copyright (c) 2020 -- Élie Michel <elie@exppad.com>
 * 
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 * 
 * The above copyright notice and this permission notice shall be included in all
 * copies or substantial portions of the Software.
 * 
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
 * SOFTWARE.
 */

using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using UnityEngine;

/// <summary>
/// Non-manifold boundary representation of a 3D mesh with arbitrary attributes.
/// This structure intends to make procedural mesh creation and arbitrary edits
/// as easy as possible while remaining efficient enough. See other comments
/// along the file to see how to use it.
///
/// This file only contains the data structure and basic operations such as
/// adding/removing elements. For more advanced operations, see BMeshOperators.
/// For operations related to Unity, like converting to UnityEngine.Mesh, see
/// the BMeshUnity class.
///
/// The basic structure is described in the paper:
///
///     Gueorguieva, Stefka and Marcheix, Davi. 1994. "Non-manifold boundary
///     representation for solid modeling."
///
/// We use the same terminology as Blender's dev documentation:
///     https://wiki.blender.org/wiki/Source/Modeling/BMesh/Design
///
/// Arbitrary attributes can be attached topological entity, namely vertices,
/// edges, loops and faces. If you are used to Houdini's terminology, note that
/// what is called "vertex" here corresponds to Houdini's points, while what
/// Houdini calls "vertex" is close to BMesh's "loops".
///
/// NB: The only dependency to Unity is the Vector3 structure, it can easily be
/// made independent.
///
/// NB: This class is not totally protected from misuse. We prefered fostering
/// ease of use over safety, so take care when you start feeling that you are
/// not fully understanding what you are doing, you'll likely mess with the
/// structure. For instance, do not add edges directly to the mesh.edges
/// list but use AddEdge, etc.
///
/// NB: This file is long, use the #region to fold it conveniently.
/// </summary>
public class BMesh
{
    // Topological entities
    public List<Vertex> vertices;
    public List<Edge> edges;
    public List<Loop> loops;
    public List<Face> faces;

    // Attribute definitions. The content of attributes is stored in the
    // topological objects (Vertex, Edge, etc.) in the 'attribute' field.
    // These lists are here to ensure consistency.
    public List<AttributeDefinition> vertexAttributes;
    public List<AttributeDefinition> edgeAttributes;
    public List<AttributeDefinition> loopAttributes;
    public List<AttributeDefinition> faceAttributes;

    public BMesh()
    {
        vertices = new List<Vertex>();
        loops = new List<Loop>();
        edges = new List<Edge>();
        faces = new List<Face>();

        vertexAttributes = new List<AttributeDefinition>();
        edgeAttributes = new List<AttributeDefinition>();
        loopAttributes = new List<AttributeDefinition>();
        faceAttributes = new List<AttributeDefinition>();
    }

    ///////////////////////////////////////////////////////////////////////////
    #region [Topology Types]

    /*
     * NB: For all topology types, except for members marked as "attribute",
     * all classes only store references to objects allocated by BMesh itself.
     * Variables marked as attributes have no impact on the topological
     * algorithm applied to the mesh (e.g. finding the neighbors), they are
     * only set by calling code. In particuler, the 'id' field has no meaning
     * for this class, it is put here purely for commodity, but don't expect it
     * to get actually filled unless you explicitely did do.
     */

    /// <summary>
    /// A vertex corresponds roughly to a position in space. Many primitives
    /// (edges, faces) can share a given vertex. Several vertices can be located
    /// at the very same position.
    /// It references a chained list of the edges that use it, embeded inside the Edge
    /// structure (see below, and see implementation of NeighborEdges).
    /// The vertex position does not affect topological algorithms, but is used by
    /// commodity functions that help finding the center of an edge or a face.
    /// </summary>
    public class Vertex
    {
        public int id; // [attribute]
        public Vector3 point; // [attribute]
        public Dictionary<string, AttributeValue> attributes; // [attribute] (extra attributes)
        public Edge edge; // one of the edges using this vertex as origin, navigates other using edge.next1/next2

        public Vertex(Vector3 _point)
        {
            point = _point;
        }

        /// <summary>
        /// List all edges reaching this vertex.
        /// </summary>
        public List<Edge> NeighborEdges()
        {
            var edges = new List<Edge>();
            if (this.edge != null)
            {
                Edge it = this.edge;
                do
                {
                    edges.Add(it);
                    it = it.Next(this);
                } while (it != edge);
            }
            return edges;
        }

        /*
         * Return true if this vertex lies on the boundary of the mesh.
         */
        public bool IsOnBoundary()
        {
            if (this.edge == null)
                return false;
            Edge it = this.edge;
            do
            {
                if (it.IsOnBoundary())
                    return true;
                it = it.Next(this);
            } while (it != edge);
            return false;
        }

        /// <summary>
        /// Return all faces that use this vertex as a corner.
        /// </summary>
        public List<Face> NeighborFaces()
        {
            var faces = new HashSet<Face>();
            if (edge != null)
            {
                Edge it = edge;
                do
                {
                    foreach (var f in it.NeighborFaces())
                    {
                        faces.Add(f);
                    }
                    it = it.Next(this);
                } while (it != edge);
            }
            return faces.ToList();
        }
    }

    /// <summary>
    /// An edge links to vertices together, and may or may not be part of a face.
    /// An edge can be shared by several faces.
    ///
    /// Technical Note: The structure stores a reference to the two vertices.
    /// Although the role of these two vertices is perfectly symmetrical, this
    /// makes the iterations over linked list slightly trickier than expected.
    ///
    /// The edge is a node of two (double) linked lists at the same time. Let's
    /// recall that a (simply) linked list of Stuff is made of nodes of the form
    ///     Node {
    ///         Stuff value;
    ///         Node next;
    ///     }
    /// Here we provide two "next", depending on whether the vertex that we are
    /// interested in is vertex1 or vertex2. Note that a vertex stored in the
    /// "vertex1" field for one edge might be stored in the "vertex2" of the
    /// next one, so the function Next() is provided to return either next1 or
    /// next2 depending on the vertex of interest.
    /// </summary>
    public class Edge
    {
        public int id; // [attribute]
        public Dictionary<string, AttributeValue> attributes; // [attribute] (extra attributes)
        public Vertex vert1;
        public Vertex vert2;
        public Edge next1; // next edge around vert1. If you don't know whether your vertex is vert1 or vert2, use Next(v)
        public Edge next2; // next edge around vert1
        public Edge prev1;
        public Edge prev2;
        public Loop loop; // first node of the list of faces that use this edge. Navigate list using radial_next

        /// <summary>
        /// Tells whether a vertex is one of the extremities of this edge.
        /// </summary>
        public bool ContainsVertex(Vertex v)
        {
            return v == vert1 || v == vert2;
        }

        /// <summary>
        /// Tells whether a face uses this edge as a side.
        /// </summary>
        public bool ContainsFace(Face f)
        {
            if (this.loop == null) return false;
            Loop it = this.loop;
            do
            {
                if (it.face == f) return true;
                it = it.radial_next;
            } while (it != this.loop);
            return false;
        }

        /// <summary>
        /// Return true if this edge is on the boundary (has only 1 adjacent face).
        /// </summary>
        public bool IsOnBoundary()
        {
            return this.loop != null && this.loop.radial_next == this.loop;
        }

        /// <summary>
        /// If one gives a vertex of the edge to this function, it returns the
        /// other vertex of the edge. Otherwise, the behavior is undefined.
        /// </summary>
        public Vertex OtherVertex(Vertex v)
        {
            Debug.Assert(ContainsVertex(v));
            return v == vert1 ? vert2 : vert1;
        }

        /// <summary>
        /// If one gives a vertex of the edge to this function, it returns the
        /// next edge in the linked list of edges that use this vertex.
        /// </summary>
        public Edge Next(Vertex v)
        {
            Debug.Assert(ContainsVertex(v));
            return v == vert1 ? next1 : next2;
        }

        /// <summary>
        /// This is used when inserting a new Edge in the lists.
        /// </summary>
        public void SetNext(Vertex v, Edge other)
        {
            Debug.Assert(ContainsVertex(v));
            if (v == vert1) next1 = other;
            else next2 = other;
        }

        /// <summary>
        /// Similar to Next() but to go backward in the double-linked list
        /// </summary>
        public Edge Prev(Vertex v)
        {
            Debug.Assert(ContainsVertex(v));
            return v == vert1 ? prev1 : prev2;
        }

        /// <summary>
        /// Similar to SetNext()
        /// </summary>
        public void SetPrev(Vertex v, Edge other)
        {
            Debug.Assert(ContainsVertex(v));
            if (v == vert1) prev1 = other;
            else prev2 = other;
        }

        /// <summary>
        /// Return all faces that use this edge as a side.
        /// </summary>
        public List<Face> NeighborFaces()
        {
            var faces = new List<Face>();
            if (this.loop != null)
            {
                var it = this.loop;
                do
                {
                    faces.Add(it.face);
                    it = it.radial_next;
                } while (it != this.loop);
            }
            return faces;
        }

        /// <summary>
        /// Compute the barycenter of the edge's vertices
        /// </summary>
        public Vector3 Center()
        {
            return (vert1.point + vert2.point) * 0.5f;
        }
    }

    /// <summary>
    /// Since a face is basically a list of edges, and the Loop object is a node
    /// of this list, called so because the list must loop.
    /// A loop is associated to one and only one face.
    ///
    /// A loop can be seen as a list of edges, it also stores a reference to a
    /// vertex for commodity but technically it could be found through the edge.
    /// It may also be interpreted as a "face corner", and is hence where one
    /// typically stores UVs, because one vertex may have different UV
    /// coordinates depending on the face.
    ///
    /// On top of this, the loop is also used as a node of another linked list,
    /// namely the radial list, that enables iterating over all the faces using
    /// the same edge.
    /// </summary>
    public class Loop
    {
        public Dictionary<string, AttributeValue> attributes; // [attribute] (extra attributes)

        public Vertex vert;
        public Edge edge;
        public Face face; // there is exactly one face using a loop

        public Loop radial_prev; // around edge
        public Loop radial_next;
        public Loop prev; // around face
        public Loop next;

        public Loop(Vertex v, Edge e, Face f)
        {
            vert = v;
            SetEdge(e);
            SetFace(f);
        }

        /// <summary>
        /// Insert the loop in the linked list of the face.
        /// (Used in constructor)
        /// </summary>
        public void SetFace(Face f)
        {
            Debug.Assert(this.face == null);
            if (f.loop == null)
            {
                f.loop = this;
                this.next = this.prev = this;
            }
            else
            {
                this.prev = f.loop;
                this.next = f.loop.next;

                f.loop.next.prev = this;
                f.loop.next = this;

                f.loop = this;
            }
            this.face = f;
        }

        /// <summary>
        /// Insert the loop in the radial linked list.
        /// (Used in constructor)
        /// </summary>
        public void SetEdge(Edge e)
        {
            Debug.Assert(this.edge == null);
            if (e.loop == null)
            {
                e.loop = this;
                this.radial_next = this.radial_prev = this;
            }
            else
            {
                this.radial_prev = e.loop;
                this.radial_next = e.loop.radial_next;

                e.loop.radial_next.radial_prev = this;
                e.loop.radial_next = this;

                e.loop = this;
            }
            this.edge = e;
        }

        /// <summary>
        /// Index of this loop within its face, counting next steps from face.loop.
        /// Matches the order of face.NeighborVertices() and face.NeighborEdges().
        /// Returns -1 if the loop has no face.
        /// </summary>
        public int IndexInFace()
        {
            if (face == null || face.loop == null) return -1;
            int i = 0;
            Loop it = face.loop;
            do
            {
                if (it == this) return i;
                it = it.next;
                i++;
            } while (it != face.loop);
            return -1;
        }
    }

    /// <summary>
    /// A face is almost nothing more than a loop. Having a different structure
    /// makes sense only 1. for clarity, because loops are a less intuitive
    /// object and 2. to store face attributes.
    /// </summary>
    public class Face
    {
        public int id; // [attribute]
        public Dictionary<string, AttributeValue> attributes; // [attribute] (extra attributes)
        public int vertcount; // stored for commodity, can be recomputed easily
        public Loop loop; // navigate list using next

        /// <summary>
        /// Get the list of vertices used by the face, ordered.
        /// </summary>
        public List<Vertex> NeighborVertices()
        {
            var verts = new List<Vertex>();
            if (this.loop != null)
            {
                Loop it = this.loop;
                do
                {
                    verts.Add(it.vert);
                    it = it.next;
                } while (it != this.loop);
            }
            return verts;
        }

        /// <summary>
        /// Assuming the vertex is part of the face, return the loop such that
        /// loop.vert = v. Return null otherwise.
        /// </summary>
        public Loop Loop(Vertex v)
        {
            if (this.loop != null)
            {
                Loop it = this.loop;
                do
                {
                    Debug.Assert(it != null);
                    if (it.vert == v) return it;
                    it = it.next;
                } while (it != this.loop);
            }
            return null;
        }

        /// <summary>
        /// Get the list of edges around the face.
        /// It is garrantied to match the order of NeighborVertices(), so that
        /// edge[0] = vert[0]--&gt;vert[1], edge[1] = vert[1]--&gt;vert[2], etc.
        /// </summary>
        public List<Edge> NeighborEdges()
        {
            var edges = new List<Edge>();
            if (this.loop != null)
            {
                Loop it = this.loop;
                do
                {
                    edges.Add(it.edge);
                    it = it.next;
                } while (it != this.loop);
            }
            return edges;
        }

        /// <summary>
        /// Compute the barycenter of the face vertices
        /// </summary>
        public Vector3 Center()
        {
            Vector3 p = Vector3.zero;
            float sum = 0;
            foreach (var v in NeighborVertices())
            {
                p += v.point;
                sum += 1;
            }
            return p / sum;
        }
    }
    #endregion

    ///////////////////////////////////////////////////////////////////////////
    #region [Topology Methods]

    /// <summary>
    /// Add a new vertex to the mesh.
    /// </summary>
    public Vertex AddVertex(Vertex vert)
    {
        EnsureVertexAttributes(vert);
        vertices.Add(vert);
        return vert;
    }
    public Vertex AddVertex(Vector3 point)
    {
        return AddVertex(new Vertex(point));
    }
    public Vertex AddVertex(float x, float y, float z)
    {
        return AddVertex(new Vector3(x, y, z));
    }

    /// <summary>
    /// Add a new edge between two vertices. If there is already such edge,
    /// return it without adding a new one.
    /// If the vertices are not part of the mesh, the behavior is undefined.
    /// </summary>
    public Edge AddEdge(Vertex vert1, Vertex vert2)
    {
        Debug.Assert(vert1 != vert2);

        var edge = FindEdge(vert1, vert2);
        if (edge != null) return edge;

        edge = new Edge
        {
            vert1 = vert1,
            vert2 = vert2
        };
        EnsureEdgeAttributes(edge);
        edges.Add(edge);

        // Insert in vert1's edge list
        if (vert1.edge == null)
        {
            vert1.edge = edge;
            edge.next1 = edge.prev1 = edge;
        }
        else
        {
            edge.next1 = vert1.edge.Next(vert1);
            edge.prev1 = vert1.edge;
            edge.next1.SetPrev(vert1, edge);
            edge.prev1.SetNext(vert1, edge);
        }

        // Same for vert2 -- TODO avoid code duplication
        if (vert2.edge == null)
        {
            vert2.edge = edge;
            edge.next2 = edge.prev2 = edge;
        }
        else
        {
            edge.next2 = vert2.edge.Next(vert2);
            edge.prev2 = vert2.edge;
            edge.next2.SetPrev(vert2, edge);
            edge.prev2.SetNext(vert2, edge);
        }

        return edge;
    }

    public Edge AddEdge(int v1, int v2)
    {
        return AddEdge(vertices[v1], vertices[v2]);
    }

    /// <summary>
    /// Add a new face that connects the array of vertices provided.
    /// The vertices must be part of the mesh, otherwise the behavior is
    /// undefined.
    /// NB: There is no AddLoop, because a loop is an element of a face
    /// </summary>
    public Face AddFace(Vertex[] fVerts)
    {
        if (fVerts.Length == 0) return null;
        foreach (var v in fVerts) Debug.Assert(v != null);

        var fEdges = new Edge[fVerts.Length];

        int i, i_prev = fVerts.Length - 1;
        for (i = 0; i < fVerts.Length; ++i)
        {
            fEdges[i_prev] = AddEdge(fVerts[i_prev], fVerts[i]);
            i_prev = i;
        }

        var f = new Face();
        EnsureFaceAttributes(f);
        faces.Add(f);

        for (i = 0; i < fVerts.Length; ++i)
        {
            Loop loop = new Loop(fVerts[i], fEdges[i], f);
            EnsureLoopAttributes(loop);
            loops.Add(loop);
        }

        f.vertcount = fVerts.Length;
        return f;
    }

    public Face AddFace(Vertex v0, Vertex v1)
    {
        return AddFace(new Vertex[] { v0, v1 });
    }

    public Face AddFace(Vertex v0, Vertex v1, Vertex v2)
    {
        return AddFace(new Vertex[] { v0, v1, v2 });
    }

    public Face AddFace(Vertex v0, Vertex v1, Vertex v2, Vertex v3)
    {
        return AddFace(new Vertex[] { v0, v1, v2, v3 });
    }

    public Face AddFace(int i0, int i1)
    {
        return AddFace(new Vertex[] { vertices[i0], vertices[i1] });
    }

    public Face AddFace(int i0, int i1, int i2)
    {
        return AddFace(new Vertex[] { vertices[i0], vertices[i1], vertices[i2] });
    }

    public Face AddFace(int i0, int i1, int i2, int i3)
    {
        return AddFace(new Vertex[] { vertices[i0], vertices[i1], vertices[i2], vertices[i3] });
    }

    /// <summary>
    /// Return an edge that links vert1 to vert2 in the mesh (an arbitrary one
    /// if there are several such edges, which is possible with this structure).
    /// Return null if there is no edge between vert1 and vert2 in the mesh.
    /// </summary>
    public Edge FindEdge(Vertex vert1, Vertex vert2)
    {
        Debug.Assert(vert1 != vert2);
        if (vert1.edge == null || vert2.edge == null) return null;

        Edge e1 = vert1.edge;
        Edge e2 = vert2.edge;
        do
        {
            if (e1.ContainsVertex(vert2)) return e1;
            if (e2.ContainsVertex(vert1)) return e2;
            e1 = e1.Next(vert1);
            e2 = e2.Next(vert2);
        } while (e1 != vert1.edge && e2 != vert2.edge);
        return null;
    }

    /// <summary>
    /// Remove the provided vertex from the mesh.
    /// Removing a vertex also removes all the edges/loops/faces that use it.
    /// If the vertex was not part of this mesh, the behavior is undefined.
    /// </summary>
    public void RemoveVertex(Vertex v)
    {
        while (v.edge != null)
        {
            RemoveEdge(v.edge);
        }

        vertices.Remove(v);
    }

    /// <summary>
    /// Remove the provided edge from the mesh.
    /// Removing an edge also removes all associated loops/faces.
    /// If the edge was not part of this mesh, the behavior is undefined.
    /// </summary>
    public void RemoveEdge(Edge e)
    {
        while (e.loop != null)
        {
            RemoveLoop(e.loop);
        }

        // Remove reference in vertices
        if (e == e.vert1.edge) e.vert1.edge = e.next1 != e ? e.next1 : null;
        if (e == e.vert2.edge) e.vert2.edge = e.next2 != e ? e.next2 : null;

        // Remove from linked lists
        e.prev1.SetNext(e.vert1, e.next1);
        e.next1.SetPrev(e.vert1, e.prev1);

        e.prev2.SetNext(e.vert2, e.next2);
        e.next2.SetPrev(e.vert2, e.prev2);

        edges.Remove(e);
    }

    /// <summary>
    /// Removing a loop also removes associated face.
    /// used internally only, just RemoveFace(loop.face) outside of here.
    /// </summary>
    void RemoveLoop(Loop l)
    {
        if (l.face != null) // null iff loop is called from RemoveFace
        {
            // Trigger removing other loops, and this one again with l.face == null
            RemoveFace(l.face);
            return;
        }

        // remove from radial linked list
        if (l.radial_next == l)
        {
            l.edge.loop = null;
        }
        else
        {
            l.radial_prev.radial_next = l.radial_next;
            l.radial_next.radial_prev = l.radial_prev;
            if (l.edge.loop == l)
            {
                l.edge.loop = l.radial_next;
            }
        }

        // forget other loops of the same face so thet they get released from memory
        l.next = null;
        l.prev = null;

        loops.Remove(l);
    }

    /// <summary>
    /// Remove the provided face from the mesh.
    /// If the face was not part of this mesh, the behavior is undefined.
    /// (actually almost ensured to be a true mess, but do as it pleases you :D)
    /// </summary>
    public void RemoveFace(Face f)
    {
        Loop l = f.loop;
        Loop nextL = null;
        while (nextL != f.loop)
        {
            nextL = l.next;
            l.face = null; // prevent infinite recursion, because otherwise RemoveLoop calls RemoveFace
            RemoveLoop(l);
            l = nextL;
        }
        faces.Remove(f);
    }
    #endregion

    ///////////////////////////////////////////////////////////////////////////
    #region [Attributes Types]

    /*
     * Attributes are arbitrary data that can be attached to topologic entities.
     * There are identified by a name and their value is an array of either int
     * or float. This array has theoretically a fixed size but in practice you
     * can do whatever you want becase they are stored per entity, not in a
     * global buffer, so it is flexible. Maybe one day for better efficiency
     * they would use proper data buffers, but the API would change anyway at
     * that point.
     */

    public enum AttributeBaseType
    {
        Int,
        Float,
    }

    /// <summary>
    /// Attribute type is used when declaring new attributes to be automatically
    /// attached to topological entities, using Add*Attributes() methods.
    /// </summary>
    public class AttributeType
    {
        public AttributeBaseType baseType;
        public int dimensions;

        /// <summary>
        /// Checks whether a given value matches this type.
        /// </summary>
        public bool CheckValue(AttributeValue value)
        {
            Debug.Assert(dimensions > 0);
            switch (baseType)
            {
                case AttributeBaseType.Int:
                    {
                        var valueAsInt = value as IntAttributeValue;
                        return valueAsInt != null && valueAsInt.data.Length == dimensions;
                    }
                case AttributeBaseType.Float:
                    {
                        var valueAsFloat = value as FloatAttributeValue;
                        return valueAsFloat != null && valueAsFloat.data.Length == dimensions;
                    }
                default:
                    Debug.Assert(false);
                    return false;
            }
        }
    }

    /// <summary>
    /// The generic class of values stored in the attribute dictionnary in each
    /// topologic entity. It contains an array of float or int, depending on its
    /// type.
    /// </summary>
    public class AttributeValue
    {
        /// <summary>
        /// Deep copy of an attribute value.
        /// </summary>
        public static AttributeValue Copy(AttributeValue value)
        {
            if (value is IntAttributeValue valueAsInt)
            {
                var data = new int[valueAsInt.data.Length];
                valueAsInt.data.CopyTo(data, 0);
                return new IntAttributeValue { data = data };
            }
            if (value is FloatAttributeValue valueAsFloat)
            {
                var data = new float[valueAsFloat.data.Length];
                valueAsFloat.data.CopyTo(data, 0);
                return new FloatAttributeValue { data = data };
            }
            Debug.Assert(false);
            return null;
        }

        /// <summary>
        /// Measure the euclidean distance between two attributes, which is set
        /// to infinity if they have different types (int or float / dimension)
        /// </summary>
        public static float Distance(AttributeValue value1, AttributeValue value2)
        {
            if (value1 is IntAttributeValue value1AsInt)
            {
                if (value2 is IntAttributeValue value2AsInt)
                {
                    return IntAttributeValue.Distance(value1AsInt, value2AsInt);
                }
            }
            if (value1 is FloatAttributeValue value1AsFloat)
            {
                if (value2 is FloatAttributeValue value2AsFloat)
                {
                    return FloatAttributeValue.Distance(value1AsFloat, value2AsFloat);
                }
            }
            return float.PositiveInfinity;
        }

        /// <summary>
        /// Cast to FloatAttributeValue (return null if it was not actually a
        /// float attribute).
        /// </summary>
        public FloatAttributeValue asFloat()
        {
            return this as FloatAttributeValue;
        }

        /// <summary>
        /// Cast to IntAttributeValue (return null if it was not actually an
        /// integer attribute).
        /// </summary>
        public IntAttributeValue asInt()
        {
            return this as IntAttributeValue;
        }
    }
    public class IntAttributeValue : AttributeValue
    {
        public int[] data;

        public IntAttributeValue() { }
        public IntAttributeValue(int i)
        {
            data = new int[] { i };
        }
        public IntAttributeValue(int i0, int i1)
        {
            data = new int[] { i0, i1 };
        }

        public static float Distance(IntAttributeValue value1, IntAttributeValue value2)
        {
            int n = value1.data.Length;
            if (n != value2.data.Length) return float.PositiveInfinity;
            float s = 0;
            for (int i = 0; i < n; ++i)
            {
                float diff = value1.data[i] - value2.data[i];
                s += diff * diff;
            }
            return Mathf.Sqrt(s);
        }
    }
    public class FloatAttributeValue : AttributeValue
    {
        public float[] data;

        public FloatAttributeValue() { }
        public FloatAttributeValue(float f)
        {
            data = new float[] { f };
        }
        public FloatAttributeValue(float f0, float f1)
        {
            data = new float[] { f0, f1 };
        }
        public FloatAttributeValue(Vector3 v)
        {
            data = new float[] { v.x, v.y, v.z };
        }

        public void FromVector2(Vector2 v)
        {
            data[0] = v.x;
            data[1] = v.y;
        }
        public void FromVector3(Vector3 v)
        {
            data[0] = v.x;
            data[1] = v.y;
            data[2] = v.z;
        }
        public void FromColor(Color c)
        {
            data[0] = c.r;
            data[1] = c.g;
            data[2] = c.b;
            data[3] = c.a;
        }

        public Vector3 AsVector3()
        {
            return new Vector3(
                data.Length > 0 ? data[0] : 0,
                data.Length > 1 ? data[1] : 0,
                data.Length > 2 ? data[2] : 0
            );
        }
        public Color AsColor()
        {
            return new Color(
                data.Length > 0 ? data[0] : 0,
                data.Length > 1 ? data[1] : 0,
                data.Length > 2 ? data[2] : 0,
                data.Length > 3 ? data[3] : 1
            );
        }

        public static float Distance(FloatAttributeValue value1, FloatAttributeValue value2)
        {
            int n = value1.data.Length;
            if (n != value2.data.Length) return float.PositiveInfinity;
            float s = 0;
            for (int i = 0; i < n; ++i)
            {
                float diff = value1.data[i] - value2.data[i];
                s += diff * diff;
            }
            return Mathf.Sqrt(s);
        }
    }

    /// <summary>
    /// Attributes definitions are stored in the mesh to automatically add an
    /// attribute with a default value to all existing and added topological
    /// entities of the target type.
    /// </summary>
    public class AttributeDefinition
    {
        public string name;
        public AttributeType type;
        public AttributeValue defaultValue;

        public AttributeDefinition(string name, AttributeBaseType baseType, int dimensions)
        {
            this.name = name;
            type = new AttributeType { baseType = baseType, dimensions = dimensions };
            defaultValue = NullValue();
        }

        /// <summary>
        /// Return a null value of the target type
        /// (should arguably be in AttributeType)
        /// </summary>
        public AttributeValue NullValue()
        {
            //Debug.Assert(type.dimensions > 0);
            switch (type.baseType)
            {
                case AttributeBaseType.Int:
                    return new IntAttributeValue { data = new int[type.dimensions] };
                case AttributeBaseType.Float:
                    return new FloatAttributeValue { data = new float[type.dimensions] };
                default:
                    Debug.Assert(false);
                    return new AttributeValue();
            }
        }
    }
    #endregion
    
    ///////////////////////////////////////////////////////////////////////////
    #region [Vertex Attribute Methods]

    /*
     * The same series of method repeats for Vertices, Edges, Loops and Faces.
     * Maybe there's a nice way to factorize, but in the meantime I'll at least
     * factorize the comments, so the following work for all types of
     * topological entities.
     */

    /// <summary>
    /// Check whether the mesh as an attribute enforced to any vertices with the
    /// given name. If this is true, one can safely use v.attribtues[attribName]
    /// without checking v.attributes.ContainsKey() first.
    /// </summary>
    public bool HasVertexAttribute(string attribName)
    {
        foreach (var a in vertexAttributes)
        {
            if (a.name == attribName)
            {
                return true;
            }
        }
        return false;
    }

    public bool HasVertexAttribute(AttributeDefinition attrib)
    {
        return HasVertexAttribute(attrib.name);
    }

    /// <summary>
    /// Add a new attribute and return it, so that one can write oneliners like
    ///     AddVertexAttribute("foo", Float, 3).defaultValue = ...
    /// NB: It does not return the attribute from the mesh definition if it
    /// existed already. Maybe this can be considered as a bug, maybe not.
    /// </summary>
    public AttributeDefinition AddVertexAttribute(AttributeDefinition attrib)
    {
        if (HasVertexAttribute(attrib)) return attrib; // !!
        vertexAttributes.Add(attrib);
        foreach (Vertex v in vertices)
        {
            if (v.attributes == null) v.attributes = new Dictionary<string, AttributeValue>(); // move in Vertex ctor?
            v.attributes[attrib.name] = AttributeValue.Copy(attrib.defaultValue);
        }
        return attrib;
    }

    public AttributeDefinition AddVertexAttribute(string name, AttributeBaseType baseType, int dimensions)
    {
        return AddVertexAttribute(new AttributeDefinition(name, baseType, dimensions));
    }

    /// <summary>
    /// Called internally when adding a new vertex to ensure that the vertex has
    /// all required attribute. If not, the default value is used to add it.
    /// </summary>
    void EnsureVertexAttributes(Vertex v)
    {
        if (v.attributes == null) v.attributes = new Dictionary<string, AttributeValue>();
        foreach (var attr in vertexAttributes)
        {
            if (!v.attributes.ContainsKey(attr.name))
            {
                v.attributes[attr.name] = AttributeValue.Copy(attr.defaultValue);
            }
            else if (!attr.type.CheckValue(v.attributes[attr.name]))
            {
                Debug.LogWarning("Vertex attribute '" + attr.name + "' is not compatible with mesh attribute definition, ignoring.");
                // different type, overriding value with default
                v.attributes[attr.name] = AttributeValue.Copy(attr.defaultValue);
            }
        }
    }
    #endregion

    ///////////////////////////////////////////////////////////////////////////
    #region [Edge Attribute Methods]

    public bool HasEdgeAttribute(string attribName)
    {
        foreach (var a in edgeAttributes)
        {
            if (a.name == attribName)
            {
                return true;
            }
        }
        return false;
    }

    public bool HasEdgeAttribute(AttributeDefinition attrib)
    {
        return HasEdgeAttribute(attrib.name);
    }

    public AttributeDefinition AddEdgeAttribute(AttributeDefinition attrib)
    {
        if (HasEdgeAttribute(attrib)) return attrib;
        edgeAttributes.Add(attrib);
        foreach (Edge e in edges)
        {
            if (e.attributes == null) e.attributes = new Dictionary<string, AttributeValue>(); // move in Edge ctor?
            e.attributes[attrib.name] = AttributeValue.Copy(attrib.defaultValue);
        }
        return attrib;
    }

    public AttributeDefinition AddEdgeAttribute(string name, AttributeBaseType baseType, int dimensions)
    {
        return AddEdgeAttribute(new AttributeDefinition(name, baseType, dimensions));
    }

    void EnsureEdgeAttributes(Edge e)
    {
        if (e.attributes == null) e.attributes = new Dictionary<string, AttributeValue>();
        foreach (var attr in edgeAttributes)
        {
            if (!e.attributes.ContainsKey(attr.name))
            {
                e.attributes[attr.name] = AttributeValue.Copy(attr.defaultValue);
            }
            else if (!attr.type.CheckValue(e.attributes[attr.name]))
            {
                Debug.LogWarning("Edge attribute '" + attr.name + "' is not compatible with mesh attribute definition, ignoring.");
                // different type, overriding value with default
                e.attributes[attr.name] = AttributeValue.Copy(attr.defaultValue);
            }
        }
    }
    #endregion

    ///////////////////////////////////////////////////////////////////////////
    #region [Loop Attribute Methods]

    public bool HasLoopAttribute(string attribName)
    {
        foreach (var a in loopAttributes)
        {
            if (a.name == attribName)
            {
                return true;
            }
        }
        return false;
    }

    public bool HasLoopAttribute(AttributeDefinition attrib)
    {
        return HasLoopAttribute(attrib.name);
    }

    public AttributeDefinition AddLoopAttribute(AttributeDefinition attrib)
    {
        if (HasLoopAttribute(attrib)) return attrib;
        loopAttributes.Add(attrib);
        foreach (Loop l in loops)
        {
            if (l.attributes == null) l.attributes = new Dictionary<string, AttributeValue>(); // move in Loop ctor?
            l.attributes[attrib.name] = AttributeValue.Copy(attrib.defaultValue);
        }
        return attrib;
    }

    public AttributeDefinition AddLoopAttribute(string name, AttributeBaseType baseType, int dimensions)
    {
        return AddLoopAttribute(new AttributeDefinition(name, baseType, dimensions));
    }

    void EnsureLoopAttributes(Loop l)
    {
        if (l.attributes == null) l.attributes = new Dictionary<string, AttributeValue>();
        foreach (var attr in loopAttributes)
        {
            if (!l.attributes.ContainsKey(attr.name))
            {
                l.attributes[attr.name] = AttributeValue.Copy(attr.defaultValue);
            }
            else if (!attr.type.CheckValue(l.attributes[attr.name]))
            {
                Debug.LogWarning("Loop attribute '" + attr.name + "' is not compatible with mesh attribute definition, ignoring.");
                // different type, overriding value with default
                l.attributes[attr.name] = AttributeValue.Copy(attr.defaultValue);
            }
        }
    }
    #endregion

    ///////////////////////////////////////////////////////////////////////////
    #region [Face Attribute Methods]

    public bool HasFaceAttribute(string attribName)
    {
        foreach (var a in faceAttributes)
        {
            if (a.name == attribName)
            {
                return true;
            }
        }
        return false;
    }

    public bool HasFaceAttribute(AttributeDefinition attrib)
    {
        return HasFaceAttribute(attrib.name);
    }

    public AttributeDefinition AddFaceAttribute(AttributeDefinition attrib)
    {
        if (HasFaceAttribute(attrib)) return attrib;
        faceAttributes.Add(attrib);
        foreach (Face f in faces)
        {
            if (f.attributes == null) f.attributes = new Dictionary<string, AttributeValue>(); // move in Face ctor?
            f.attributes[attrib.name] = AttributeValue.Copy(attrib.defaultValue);
        }
        return attrib;
    }

    public AttributeDefinition AddFaceAttribute(string name, AttributeBaseType baseType, int dimensions)
    {
        return AddFaceAttribute(new AttributeDefinition(name, baseType, dimensions));
    }

    void EnsureFaceAttributes(Face f)
    {
        if (f.attributes == null) f.attributes = new Dictionary<string, AttributeValue>();
        foreach (var attr in faceAttributes)
        {
            if (!f.attributes.ContainsKey(attr.name))
            {
                f.attributes[attr.name] = AttributeValue.Copy(attr.defaultValue);
            }
            else if (!attr.type.CheckValue(f.attributes[attr.name]))
            {
                Debug.LogWarning("Face attribute '" + attr.name + "' is not compatible with mesh attribute definition, ignoring.");
                // different type, overriding value with default
                f.attributes[attr.name] = AttributeValue.Copy(attr.defaultValue);
            }
        }
    }
    #endregion
}
