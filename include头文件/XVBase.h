
#ifndef XVBASE_H
#define XVBASE_H

#include <vector>
using namespace std;

typedef unsigned char BYTE;
typedef int			  BOOL;
#define TRUE	1
#define FALSE	0

namespace XVL
{
	enum XVOptionalType                     //使能
	{
		NUL,
		ENABLE
	};

	typedef struct XVInt
	{
		XVOptionalType  optional;
		int value;
	}XVInt;

	typedef struct XVFloat
	{
		XVOptionalType  optional;
		float value;
	}XVFloat;

	struct XVLocation
	{
		int x;
		int y;
	};

	//2D point
	struct XVPoint2DInt
	{
		int x;
		int y;
	};

	struct XVPoint2D
	{
		float x;
		float y;
	};
	//3D point
	struct XVPoint3D
	{
		float x;
		float y;
		float z;
	};

	//Circle
	struct XVCircle2D
	{
		XVPoint2D center;                  //center
		float radius;                      //radius
	};

	//CircleField
	struct XVCircleFittingField
	{
		XVCircle2D	axis;                  //扫描中心轴
		float		width;                 //扫描宽度
	};

	//Arc
	struct XVArc2D
	{
		XVPoint2D center;                  //中心
		float radius;                      //半径
		float startAngle;                  //起始角度
		float sweepAngle;                  //扫描角度
	};

	//圆弧域
	struct XVArcFittingField
	{
		XVArc2D	axis;
		float	width; 
	};

	//矩形及其旋转角度
	typedef struct XVRectangle2D
	{
		XVPoint2D origin;                  //顶点(左上点，不是中心)
		float angle;                       //旋转角度（绕中心旋转角度）
		float width;                       //宽度
		float height;                      //高度
	}XVRectangle2D;

	typedef struct XVRectangle2DInt
	{
		XVPoint2DInt origin;               //顶点
		float angle;                       //旋转角度
		int width;                         //宽度
		int height;                        //高度
	}XVRectangle2DInt;

	//Box
	typedef struct XVBox
	{
		XVPoint2D origin;                  //顶点(左上点，不是中心)
		float width;                       //宽度
		float height;                      //高度
	}XVBox;

	//边缘跳变类型
	enum XVEdgeTransitionType
	{
		BrightToDark,                      //由白到黑
		DarkToBright,                      //由黑到白
		Any                                // 任意
	};

	//表示一个边缘点
	struct XVEdge1D
	{
		XVPoint2D			    point;     //坐标
		float			        magnitude; //梯度幅值
		XVEdgeTransitionType	transition;//方向
	};

	//表示边缘点之间的距离
	struct XVGap1D
	{
		XVPoint2D	point1;
		XVPoint2D	point2;
		float	width;
	};

	//像素类型
	enum XVPlainType
	{
		Int8,
		UInt8,
		Int16,
		UInt16,
		Int32,
		Real
	};

	typedef struct XVImage
	{
		int				width;			// number of pixel columns
		int				height;			// number of pixel rows
		XVPlainType	    type;	        // type of channels (默认UInt8)
		int				depth;			// number of channels(灰度图1)
		int             pitch;          // number of bytes between consecutive rows
		BYTE*           data;
	}XVImage;

	//直线ax + by + c = 0
	struct XVLine2D
	{
		float a;
		float b;
		float c;
	};

	//行程
	struct XVPointRun
	{
		int    x;
		int    y;                       // PointRun begin location 起始点的x,y像素位置
		int    length;                  //行程
	};

	typedef struct XVRegion
	{
		XVOptionalType  optional;       //ROI是否起作用
		int frameWidth;
		int frameHeight;
		vector<XVPointRun> arrayPointRun;
	}XVRegion;

	//路径或者链条
	struct XVPath
	{
		vector< XVPoint2D > arrayPoint2D;
	};

	struct XVPathFittingField
	{
		XVPath	axis;
		float	width;
	};

	enum XVPolarityType
	{
		Bright,
		Dark,
		ANY
	};

	struct XVRidge1D                    //Represents ridges found by 1D Edge Detection functions
	{
		XVPoint2D		point;
		float		    magnitude;
		XVPolarityType	polarity;
	};

	struct XVStripe1D                   //Represents stripes found by 1D Edge Detection functions
	{
		XVPoint2D		point1;
		XVPoint2D	    point2;
		float			width;
		float			magnitude;
		XVPolarityType	polarity;
	};

	struct XVSegment2D                  //通过起点和终点来表示一条有向线段
	{
		XVPoint2D point1;               //起点
		XVPoint2D point2;               //终点
	};

	struct XVSegmentFittingField
	{
		XVSegment2D	axis;
		float		width;
	};

	struct XVSegmentScanField           //与XVSegmentFittingField取扫描线的方式不同
	{
		XVSegment2D	axis;
		float		width;
	};

	//表示一个局部坐标系，包括参考点、旋转角度及缩放系数（一个匹配结果，用于对模板进行变换）
	typedef struct XVCoordinateSystem2D
	{
		XVOptionalType  optional; 
		XVPoint2D       origin;
		float angle;
		float scale;
	}XVCoordinateSystem2D;

	struct XVObject2D
	{
		XVRectangle2D			match;       //一个矩形框
		XVCoordinateSystem2D	alignment;   //（定义一个局部的参考坐标系，在模板匹配中是模板之间的相互变换） 
		XVPoint2D				point;       //目标在图像中的位置
		float				    angle;       //angle和scale跟alignment中的值相同
		float				    scale;
		float				    score;

		vector<XVPath>	        objectEdges; //变换之后的模型的边缘
	};
}
#endif // XVBASE_H