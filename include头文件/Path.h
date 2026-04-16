#ifndef PATHROCESS_H
#define PATHROCESS_H
#ifdef PATHPROCESS_EXPORTS  
#define MAKEDLL_API extern "C" __declspec(dllexport)  
#else  
#define MAKEDLL_API extern "C" __declspec(dllimport)  
#endif 

/*************************************************
*
*             2D算法库测量函数头文件
*           Created on 2024.4.30 by sunhao
*
*************************************************/

#include "XVBase.h"
#include"XV2DBase.h"

namespace XVL
{
	/*****分割路径*****/

	/*权重*/
	struct XVWeights
	{
		float          weight_segmentC;//(线段组合权重)线段分割组合优化中，组合后的线段较组合前的线段的权重(range:0-1,df:1)
		float          weight_arcC;//(圆弧组合权重)线段-弧分割组合优化中，组合后的圆弧较组合前的表示(线段/圆弧)的权重(range:0-1,df:1)
		float          weight_arc;//(弧线权重)线段-弧分割中，圆弧显著性较线段显著性的权重(range:0-2,df:1)
	};

	typedef struct XVSegmentPathsIn
	{
		vector<XVPath>                   inPaths;//(输入路径)需要分割的路径
		XVSegmentMode                    inSegmentMode;//(分割模式)(df:segments)
		XVWeights                        inWeights;//(权重)
		int                              inLineCFlag;//(线段组合标志)线段分割是否进行组合优化的标志位(1:是,0:否,df:0)
		float                            inMaxDeviation;//(最大偏差)路径中的点到分割线段的最大距离(range:0-999999,df:2)
		int                              inMaxArcRadius;//(最大圆弧半径)分割圆弧的最大半径(range:0-999999,df:500)
		float                            inMaxError;//(最大误差)圆弧拟合标准差(range:0-1,df:0.8)
	}XVSegmentPathsIn;

	typedef struct XVSegmentPathsOut
	{
		vector<XVPath>                   outStraight;//(线段路径)分类为直线段的路径片段
		vector<XVPath>                   outArciform;//(圆弧路径)分类为圆弧形的路径片段
		vector<XVSegment2D>              outSegments;//(线段)与outStraight对应的拟合线段
		vector<XVArc2D>                  outArcs;//(圆弧)与outArciform对应的拟合圆弧
		float                            outTime;//(算法耗时)
	}XVSegmentPathsOut;

	/*
	* @brief  将输入路径分割成几何基元的组合
	* return  [out] 0  函数执行成功
	*               -1 输入路径为空
	*/
	MAKEDLL_API int XVSegmentPaths(XVSegmentPathsIn& segmentPathsIn, XVSegmentPathsOut& segmentPathsOut);

	//struct XVSegmentPathIn {
	//	XVPath inPath;
	//	float  inSmoothingStdDev;
	//	float  inMaxDeviation;
	//	XVSegmentMode  inSegmentMode;
	//	float  inMaxArcRadius;
	//};

	//struct XVSegmentPathOut {
	//	vector<XVPath>                   outStraight;//(线段路径)分类为直线段的路径片段
	//	vector<XVPath>                   outArciform;//(圆弧路径)分类为圆弧形的路径片段
	//	vector<XVSegment2D>              outSegments;//(线段)与outStraight对应的拟合线段
	//	vector<XVArc2D>                  outArcs;//(圆弧)与outArciform对应的拟合圆弧
	//	float                            outTime;//(算法耗时)
	//};
	///*
	//* @brief  将输入路径分割成几何基元的组合
	//* return  [out] 0  函数执行成功
	//*               -1 输入路径为空
	//*/
	//MAKEDLL_API int XVSegmentPath(XVSegmentPathIn& segmentPathIn, XVSegmentPathOut& segmentPathOut);

	/*****平滑路径*****/
	typedef struct XVSmoothPathsIn
	{
		vector<XVPath>                   inPaths;//(输入路径)
		int                              inPointNums;//(点数)用于拟合线段的点数(range:3-999999, df:5)
	}XVSmoothPathsIn;

	typedef struct XVSmoothPathsOut
	{
		vector<XVPath>                   outPaths;//(输出路径)平滑后的路径
		float                            outTime;//(算法耗时)
	}XVSmoothPathsOut;

	/*
	* @brief  平滑路径
	* return  [out] 0  函数执行成功
	*               -1 输入路径为空
	*/
	MAKEDLL_API int XVSmoothPaths(XVSmoothPathsIn& smoothPathsIn, XVSmoothPathsOut& smoothPathsOut);



	/*****筛选路径*****/

	typedef struct XVClassifyPathsIn
	{
		vector<XVPath>                   inPaths;//(输入路径)
		XVPathFilter                     inPathFilter; //(路径过滤器)确定哪些路径将参与计算(df:All)
		XVPathFeature                    inFeature;//(路径特征)(df:Length)
		float                            inMinimum;//(范围最小值)(df:0)
		float                            inMaximum;//(范围最大值)(df:99999999)
	}XVClassifyPathsIn;

	typedef struct XVClassifyPathsOut
	{
		vector<XVPath>                   outAccepted;//(接受的路径)特征值在筛选范围内的路径
		vector<XVPath>                   outRejected;//(拒绝的路径)特征值超出筛选范围的路径
		vector<float>                    outValues;//(特征值)计算出的特征值
		float                            outTime;//(算法耗时)
	}XVClassifyPathsOut;

	/*
	* @brief  依据给定特征筛选路径
	* return  [out] 0  函数执行成功
	*               -1 输入路径为空
	*/
	MAKEDLL_API int XVClassifyPaths(XVClassifyPathsIn& classifyPathsIn, XVClassifyPathsOut& classifyPathsOut);



	/*****选择最大的路径*****/
	typedef struct XVGetMaximumPathIn
	{
		vector<XVPath>                   inPaths;//(输入路径)
		XVPathFeature                    inFeature;//(路径特征)(df:Length)
	}XVGetMaximumPathIn;

	typedef struct XVGetMaximumPathOut
	{
		XVPath                           outPath;//(输出路径)
		float                            outValue;//(特征值)输出路径的计算特征值
		int                              outIndex;//(索引)输出路径的索引
		float                            outTime;//(算法耗时)
	}XVGetMaximumPathOut;

	/*
	* @brief  选择最大的路径
	* return  [out] 0  函数执行成功
	*               -1 输入路径为空
	*/
	MAKEDLL_API int XVGetMaximumPath(XVGetMaximumPathIn& getMaximumPathIn, XVGetMaximumPathOut& getMaximumPathOut);



	/*****选择最小的路径*****/
	typedef struct XVGetMinimumPathIn
	{
		vector<XVPath>                   inPaths;//(输入路径)
		XVPathFeature                    inFeature;//(路径特征)(df:Length)
	}XVGetMinimumPathIn;

	typedef struct XVGetMinimumPathOut
	{
		XVPath                           outPath;//(输出路径)
		float                            outValue;//(特征值)输出路径的计算特征值
		int                              outIndex;//(索引)输出路径的索引
		float                            outTime;//(算法耗时)
	}XVGetMinimumPathOut;

	/*
	* @brief  选择最小的路径
	* return  [out] 0  函数执行成功
	*               -1 输入路径为空
	*/
	MAKEDLL_API int XVGetMinimumPath(XVGetMinimumPathIn& getMinimumPathIn, XVGetMinimumPathOut& getMinimumPathOut);



	/*****组合相邻路径*****/
	typedef struct XVJoinAdjacentPathsIn
	{
		vector<XVPath>                   inPaths;//(输入路径)
		float                            inMaxJoinDistance;//(最大组合距离)，即两条路径可以被组合的最大距离(range:0-999999,df:10)
		float                            inMaxJoinAngle; //(最大组合角度)，即两条路径可以被组合的最大角度(range:0-180,df:30)
		float                            inJoinDistanceWeight;//(组合距离权重)，根据路径之间的角度差确定路径之间距离的重要程度(range:0-1,df:1)
		XVInt                            inJoinEndLength;//(组合长度)，确定用于计算路径角度的路径尾部长度(range:2-999999,df:NUL)
		int                              inMinLength;//(最小路径长度)，即路径组合后小于该值的路径将被删除(range:0-999999,df:0)
	}XVJoinAdjacentPathsIn;

	typedef struct XVJoinAdjacentPathsOut
	{
		vector<XVPath>                   outPaths;//(输出路径)
		float                            outTime;//(算法耗时)
	}XVJoinAdjacentPathsOut;

	/*
	* @brief  组合相邻的路径
	* return  [out] 0  函数执行成功
	*               -1 输入路径为空
	*/
	MAKEDLL_API int XVJoinAdjacentPaths(XVJoinAdjacentPathsIn& joinAdjacentPathsIn, XVJoinAdjacentPathsOut& joinAdjacentPathsOut);



	/*****路径特征*****/
	/***质心***/
	typedef struct XVPathMassCenterIn
	{
		XVPath                           inPath;//(输入路径)
	}XVPathMassCenterIn;

	typedef struct XVPathMassCenterOut
	{
		XVPoint2D                        outMassCenter;//(质心)
		float                            outTime;
	}XVPathMassCenterOut;

	/***长度***/
	typedef struct XVPathLengthIn
	{
		XVPath                           inPath;//(输入路径)
	}XVPathLengthIn;

	typedef struct XVPathLengthOut
	{
		float                            outLength;//(长度)
		float                            outTime;
	}XVPathLengthOut;

	/***面积***/
	typedef struct XVPathAreaIn
	{
		XVPath                           inPath;//(输入路径)
	}XVPathsAreaIn;

	typedef struct XVPathAreaOut
	{
		float                            outArea;//(面积)
		float                            outTime;
	}XVPathAreaOut;

	/***凸包***/
	typedef struct XVPathConvexHullIn
	{
		XVPath                           inPath;//(输入路径)
	}XVPathConvexHullIn;

	typedef struct XVPathConvexHullOut
	{
		XVPath                           outConvexHull;//(凸包)
		float                            outTime;
	}XVPathConvexHullOut;

	/***最小外接框***/
	typedef struct XVPathBoundingBoxIn
	{
		XVPath                           inPath;//(输入路径)
	}XVPathBoundingBoxIn;

	typedef struct XVPathBoundingBoxOut
	{
		XVBox                            outBox;//(最小外接框)
		XVPoint2D                        leftUP;       //左上顶点
		XVPoint2D                        rightUP;      //右上顶点
		XVPoint2D                        leftDown;     //左下顶点
		XVPoint2D                        rightDown;    //右下顶点
		XVPoint2D                        outGravityCentre; //重心
		float                            outTime;
	}XVPathBoundingBoxOut;

	/***最小外接矩形***/
	typedef struct XVPathBoundingRectIn
	{
		XVPath                           inPath;//(输入路径)
		XVRectangleOrientation           inRectangleOrientation;
	}XVPathBoundingRectIn;

	typedef struct XVPathBoundingRectOut
	{
		XVRectangle2D                    outRectangle;//(最小外接矩形)
		XVPoint2D                        leftUP;       //左上顶点
		XVPoint2D                        rightUP;      //右上顶点
		XVPoint2D                        leftDown;     //左下顶点
		XVPoint2D                        rightDown;    //右下顶点
		XVPoint2D                        outGravityCentre; //重心
		float                            outTime;
	}XVPathBoundingRectOut;

	/***最小外接圆***/
	typedef struct XVPathBoundingCircleIn
	{
		XVPath                           inPath;//(输入路径)
	}XVPathBoundingCircleIn;

	typedef struct XVPathBoundingCircleOut
	{
		XVCircle2D                       outCircle;//(最小外接圆)
		float                            outTime;
	}XVPathBoundingCircleOut;

	/***凸度***/
	typedef struct XVPathConvexityIn
	{
		XVPath                           inPath;//(输入路径)
	}XVPathConvexityIn;

	typedef struct XVPathConvexityOut
	{
		float                            outConvexity;//(凸度)
		float                            outTime;
	}XVPathConvexityOut;

	/***圆度***/
	typedef struct XVPathCircularityIn
	{
		XVPath                           inPath;//(输入路径)
	}XVPathCircularityIn;

	typedef struct XVPathCircularityOut
	{
		float                            outCircularity;//(圆度)
		float                            outTime;
	}XVPathCircularityOut;

	/***矩形度***/
	typedef struct XVPathRectangularityIn
	{
		XVPath                           inPath;//(输入路径)
	}XVPathRectangularityIn;

	typedef struct XVPathRectangularityOut
	{
		float                            outRectangularity;//(矩形度)
		float                            outTime;
	}XVPathRectangularityOut;

	/***椭圆长短轴***/
	typedef struct XVPathEllipticAxesIn
	{
		XVPath                           inPath;//(输入路径)
	}XVPathEllipticAxesIn;

	typedef struct XVPathEllipticAxesOut
	{
		XVAxisInfo                       outMajorAxis;
		XVAxisInfo                       outMinorAxis;
		//XVSegment2D                      outMajorAxis;//(长轴)
		//XVSegment2D                      outMinorAxis;//(短轴)
		float                            outTime;
	}XVPathEllipticAxesOut;

	/***角度***/
	typedef struct XVPathOrientationIn
	{
		XVPath                           inPath;//(输入路径)
	}XVPathOrientationIn;

	typedef struct XVPathOrientationOut
	{
		float                            outOrientation;//(角度)
		float                            outTime;
	}XVPathOrientationOut;

	/***延伸率***/
	typedef struct XVPathElongationIn
	{
		XVPath                           inPath;//(输入路径)
	}XVPathElongationIn;

	typedef struct XVPathElongationOut
	{
		float                            outElongation;//(延伸率)
		float                            outTime;
	}XVPathElongationOut;

	/*
	* @brief  计算路径的质心
	* return  [out] 0  函数执行成功
	*               -1 输入路径为空
	*/
	MAKEDLL_API int XVPathMassCenter(XVPathMassCenterIn& pathMassCenterIn, XVPathMassCenterOut& pathMassCenterOut);
	/*
	* @brief  计算路径的长度
	* return  [out] 0  函数执行成功
	*               -1 输入路径为空
	*/
	MAKEDLL_API int XVPathLength(XVPathLengthIn& pathLengthIn, XVPathLengthOut& pathLengthOut);
	/*
	* @brief  计算路径的面积
	* return  [out] 0  函数执行成功
	*               -1 输入路径为空
	*               -2 输入路径不闭合
	*/
	MAKEDLL_API int XVPathArea(XVPathAreaIn& pathAreaIn, XVPathAreaOut& pathAreaOut);
	/*
	* @brief  计算路径的凸包
	* return  [out] 0  函数执行成功
	*               -1 输入路径为空
	*/
	MAKEDLL_API int XVPathConvexHull(XVPathConvexHullIn& pathConvexHullIn, XVPathConvexHullOut& pathConvexHullOut);
	/*
	* @brief  计算路径的最小外接框
	* return  [out] 0  函数执行成功
	*               -1 输入路径为空
	*/
	MAKEDLL_API int XVPathBoundingBox(XVPathBoundingBoxIn& pathBoundingBoxIn, XVPathBoundingBoxOut& pathBoundingBoxOut);
	/*
	* @brief  计算路径的最小外接矩形
	* return  [out] 0  函数执行成功
	*               -1 输入路径为空
	*/
	MAKEDLL_API int XVPathBoundingRect(XVPathBoundingRectIn& pathBoundingRectIn, XVPathBoundingRectOut& pathBoundingRectOut);
	/*
	* @brief  计算路径的最小外接圆
	* return  [out] 0  函数执行成功
	*               -1 输入路径为空
	*/
	MAKEDLL_API int XVPathBoundingCircle(XVPathBoundingCircleIn& pathBoundingCircleIn, XVPathBoundingCircleOut& pathBoundingCircleOut);
	/*
	* @brief  计算路径的凸度
	* return  [out] 0  函数执行成功
	*               -1 输入路径为空
	*               -2 输入路径不闭合(首尾点相同即为闭合)
	*/
	MAKEDLL_API int XVPathConvexity(XVPathConvexityIn& pathConvexityIn, XVPathConvexityOut& pathConvexityOut);
	/*
	* @brief  计算路径的圆度
	* return  [out] 0  函数执行成功
	*               -1 输入路径为空
	*               -2 输入路径不闭合
	*/
	MAKEDLL_API int XVPathCircularity(XVPathCircularityIn& pathCircularityIn, XVPathCircularityOut& pathCircularityOut);
	/*
	* @brief  计算路径的矩形度
	* return  [out] 0  函数执行成功
	*               -1 输入路径为空
	*               -2 输入路径不闭合
	*/
	MAKEDLL_API int XVPathRectangularity(XVPathRectangularityIn& pathRectangularityIn, XVPathRectangularityOut& pathRectangularityOut);
	/*
	* @brief  计算路径的椭圆长短轴
	* return  [out] 0  函数执行成功
	*               -1 输入路径为空
	*               -2 输入路径不闭合
	*/
	MAKEDLL_API int XVPathEllipticAxes(XVPathEllipticAxesIn& pathEllipticAxesIn, XVPathEllipticAxesOut& pathEllipticAxesOut);
	/*
	* @brief  计算路径的等效椭圆的角度
	* return  [out] 0  函数执行成功
	*               -1 输入路径为空
	*               -2 输入路径不闭合
	*/
	MAKEDLL_API int XVPathOrientation(XVPathOrientationIn& pathOrientationIn, XVPathOrientationOut& pathOrientationOut);
	/*
	* @brief  计算路径的延伸率
	* return  [out] 0  函数执行成功
	*               -1 输入路径为空
	*               -2 输入路径不闭合
	*/
	MAKEDLL_API int XVPathElongation(XVPathElongationIn& pathElongationIn, XVPathElongationOut& pathElongationOut);
	
	   //**************************交并差运算******************************************//
	struct OutPolygon {
		XVPath  poly;
		bool   sign;       //true为保留多边形，false为剔除多边形
		float  area;
	};

	struct PolygonOperationIn {           //多边形inPath1对多边形inPath2，求差并交
		XVPath inPath1;
		XVPath inPath2;
	};

	struct PolygonOperationOut {
		//vector<XVPath> outPoly;         //顶点序列(交并差结果)
		vector<OutPolygon> outPath;
		float outTime;
	};

	struct RectOperationIn {             //矩形inRect1对矩形inRect2，求交并差
		XVRectangle2D inRect1;
		XVRectangle2D inRect2;
	};

	struct RectOperationOut {
		// vector<XVPath> outPoly;          //顶点序列(交并差结果)
		vector<OutPolygon> outPath;
		float outTime;
	};

	//两矩形求差
	MAKEDLL_API  int RectCut(RectOperationIn& rectOperationIn, RectOperationOut& rectOperationOut);       //求差运算
	//return 0  运行成功

	//两矩形求
	MAKEDLL_API int  RectCombine(RectOperationIn& rectOperationIn, RectOperationOut& rectOperationOut);   //求并运算
	 //return 0  运行成功

	//两矩形求交
	MAKEDLL_API int RectIntersection(RectOperationIn& rectOperationIn, RectOperationOut& rectOperationOut); //求交运算
	 //return 0  运行成功

	//两多边形求差
	MAKEDLL_API  int PolygonCut(PolygonOperationIn& polygonOperationIn, PolygonOperationOut& polygonOperationOut);        //差集运算
	 //return 0  运行成功

	//两多边形求并
	MAKEDLL_API int PolygonCombine(PolygonOperationIn& polygonOperationIn, PolygonOperationOut& polygonOperationOut);     //并集运算
	 //return 0  运行成功

	//两多边形求交
	MAKEDLL_API int PolygonIntersection(PolygonOperationIn& polygonOperationIn, PolygonOperationOut& polygonOperationOut);  //交集运算
	 //return 0  运行成功
	//**************************交并差运算******************************************//

	//**************************位置关系判断******************************************//
	struct TestPointInPolygonIn {
		XVPath inPath;
		XVPoint2D  inPoint;
	};

	struct TestPointInPolygonOut {
		PointLocation outFlag;
		float outTime;
	};
	/*
	* @brief  判断点与多边形位置关系
	* return  [out] 0  函数执行成功
	*               -1  输入inPath为空
	*               -2  输入inPath不闭合(首尾点相同即为闭合)
	*/
	MAKEDLL_API int TestPointInPolygon(TestPointInPolygonIn& pointPolygonIn, TestPointInPolygonOut& pointPolygonOut);





	struct  TestPolygonInPolygonIn {
		XVPath  inPath1;         //测试形状
		XVPath  inPath2;         //测试位置形状
	};

	struct  TestPolygonInPolygonOut {
		PolygonLocation outFlag;
		float outTime;
	};
	/*
	* @brief  判断一条闭合路径与另一条闭合路径位置关系(inPath2与inPath1相离、相交、包含(指inPath2包含inPath1))
	* return  [out] 0  函数执行成功
	*               -1  输入inPath1为空或输入inPath2为空
	*               -2  输入inPath1不闭合或输入inPath2不闭合
	*/
	MAKEDLL_API int TestPolygonInPolygon(TestPolygonInPolygonIn& polygonInPolygonIn, TestPolygonInPolygonOut& polygonInPolygonOut);
	//**************************位置关系判断******************************************//


	//**************************缩放******************************************//
	struct XVPathCalcPointIn {
		XVPath inPath;
		float sec_dis;   //缩放距离[-999999,9999999]
	};
	struct XVPathCalcPointOut {
		XVPath outPath;
		float outTime;
	};

	//闭合路径等距缩放
	MAKEDLL_API int XVPathCalcPoint(XVPathCalcPointIn& calcPointIn, XVPathCalcPointOut& calcPointOut);
	//return 0  运行成功

	struct RectangleVertex {
		XVPoint2D leftUP;       //左上顶点
		XVPoint2D rightUP;      //右上顶点
		XVPoint2D leftDown;     //左下顶点
		XVPoint2D rightDown;    //右下顶点
	};
	struct XVRectangle2DToVertexIn {
		XVRectangle2D inRect;
	};
	struct XVRectangle2DToVertexOut {
		RectangleVertex outVertex;
		float outTime;
	};

	/*
	* @brief  XVRectangle2D转换成4顶点
	* return  [out] 0  函数执行成功
	*               -1 输入矩形长或宽小于0
	*/
	MAKEDLL_API int XVRectangle2DToVertex(XVRectangle2DToVertexIn& XVRectangle2DToVertex_In, XVRectangle2DToVertexOut& XVRectangle2DToVertex_Out);



	struct XVPathRectExpandIn {
		XVRectangle2D inRect;
		bool  chooseType;   //选择放缩方式false-按具体数值放缩，true-按长宽比例缩放,（df:true）
		Anchor inAnchor;    //缩放锚点选择（df:MiddleCenter）
		float Dw;    //宽度缩放量（range:负999999-999999,df:0）
		float Dh;    //高度缩放量（range:负999999-999999,df:0）
		float wProportion;  //宽度缩放比例（range:0-999999,df:1）
		float hProportion;  //高度缩放比例（range:0-999999,df:1）
	};

	struct XVPathRectExpandOut {
		XVRectangle2D outRect;
		RectangleVertex outVertex;
		float outTime;
	};
	/*
	* @brief  矩形指定长宽增量或比例进行缩放
	* return  [out] 0  函数执行成功
	*              -1  输入矩形长或宽小于0
	*/
	MAKEDLL_API int XVPathRectExpand(XVPathRectExpandIn& rectExpandIn, XVPathRectExpandOut& rectExpandOut);
	//**************************缩放******************************************//

	enum SegmentPathPosition {
		SegmentPathPosition_Inner,
		SegmentPathPosition_Outter,
		SegmentPathPosition_Intersected
	};
	struct XVPathSegmentIntersectionsIn {
		XVPath inPath;//输入路径
		XVSegment2D inSegment;//输入线段，两端点默认为(0,0)和(0,0)
	};
	struct XVPathSegmentIntersectionsOut {
		vector<XVPoint2D>outIntersectionPoints;//交点
		vector<XVPath>outPaths;//分割路径集合
		SegmentPathPosition outPosition;//位置关系
		float outTime;//时间
	};
	//函数名称：路径线段交点
	MAKEDLL_API int XVPathSegmentIntersections(XVPathSegmentIntersectionsIn& XVPathSegmentIntersections_In, XVPathSegmentIntersectionsOut& XVPathSegmentIntersections_Out);
	//返回0， 

	struct XVPathSmoothPathGaussIn {
		XVPath inPath;    //输入路径
		float inStdev;    //平滑标准偏差  (range：0-999999)(df:0.6)
		float inKernelRelativeSize;    //确定内核大小的标准偏差倍数   (range：0-999999)(df:3)
	};
	struct XVPathSmoothPathGaussOut {
		XVPath outPath;
		float outTime;//时间
	};
	/*
	* @brief  高斯路径平滑
	* return  [out] 0  函数执行成功
	*              -1  输入inPath为空
	*/
	MAKEDLL_API int XVPathSmoothPathGauss(XVPathSmoothPathGaussIn& XVPathSmoothPathGauss_In, XVPathSmoothPathGaussOut& XVPathSmoothPathGauss_Out);

	struct XVPathTurnAngleLocalMaximaIn {
		XVPath inPath;    //输入路径
		TurnDerection inTurnDerection;//Left-左转、Right-右转、或者ALL-两者(df:ALL)
		bool inMark;      //false-局部存在多个最大值，认为该点不是局部最大值，true-局部存在多个最大值，认为该点是局部最大值(df:true)
		float inMinTurnAngle;   //相关角度最小值(range：0-180)(df:30)
		float inMinDistance;    //假设每个路径段具有单位长度，则在两个局部最大值之间的路径上的最小距离(range：0-999999)(df:0)
		float inSmoothingStdDev; //输入路径的高斯平滑标准偏差(range：0-999999)(df:0.6)
	};

	struct XVPathTurnAngleLocalMaximaOut {
		XVPath outPath;    //平滑后路径
		vector<XVPoint2D> outPoint;   //局部最大值点集
		vector<int> outTurnAngleMaximaIndices;  //局部最大值点的索引值
		vector<float> outTurnAngleMaximaAngles;  //局部最大值对应的角度值
		float outTime;
	};
	/*
	* @brief  路径转弯角局部最大值
	* return  [out] 0  函数执行成功
	*              -1  输入inPath为空
	*              -2  输入inPath不闭合
	*/
	MAKEDLL_API int XVPathTurnAngleLocalMaxima(XVPathTurnAngleLocalMaximaIn& XVPathTurnAngleLocalMaxima_In, XVPathTurnAngleLocalMaximaOut& XVPathTurnAngleLocalMaxima_Out);



	struct XVPathToPathDistanceIn {
		XVPath inPath1;    //输入路径
		XVPath inPath2;    //输入路径
		PathDistanceMode inPathDistanceMode;     //计算距离方式(df:PointtoPoint)
	};

	struct XVPathToPathDistanceOut {
		float outDistance;         //输出距离
		XVSegment2D outConnectingSegment;   //输出距离的起点到终点的线段
		float outTime;
	};


	/*
	* @brief  路径到路径的距离
	* return  [out] 0  函数执行成功
	*              -1  输入路径为空
	*/
	MAKEDLL_API int XVPathToPathDistance(XVPathToPathDistanceIn& XVPathToPathDistance_In, XVPathToPathDistanceOut& XVPathToPathDistance_Out);

	struct XVTestPathClosedIn {
		XVPath inPath;    //输入路径

	};

	struct XVTestPathClosedOut {
		bool    outResult;   //判断结果（true-闭合，false-不闭合）
		XVPath  outPath;     //输出闭合路径
		float   outTime;
	};

	/*
	* @brief  判断路径是否闭合
	* return  [out] 0  函数执行成功
	*              -1  输入路径为空
	*/
	MAKEDLL_API int XVTestPathClosed(XVTestPathClosedIn& XVTestPathClosed_In, XVTestPathClosedOut& XVTestPathClosed_Out);

	struct XVApproxPolyDPIn {
		XVPath inPath;    //输入路径
		float  inEpsilon; //近似误差参数
		bool  isClose;
	};
	struct XVApproxPolyDPOut {
		XVPath outApproxCurve;    //输出路径
		float   outTime;
	};
	/*
	* @brief  路径多边形拟合(opencv版)
	* return  [out] 0  函数执行成功
	*              -1  输入路径为空
	*/
	//MAKEDLL_API int XVApproxPolyDP(XVApproxPolyDPIn& XVApproxPolyDP_In, XVApproxPolyDPOut& XVApproxPolyDP_Out);

	struct XVApproxPolyIn {
		XVPath inPath;    //输入路径
		float  inEpsilon;  //近似误差参数(range：0-999999)(df:2)
	};
	struct XVApproxPolyOut {
		XVPath outApproxCurve;    //输出路径
		float   outTime;
	};
	/*
	* @brief  路径多边形拟合(自研版)
	* return  [out] 0  函数执行成功
	*              -1  输入路径为空
	*/
	MAKEDLL_API int XVApproxPoly(XVApproxPolyIn& XVApproxPoly_In, XVApproxPolyOut& XVApproxPoly_Out);

	struct XVPathToPathDistanceProfileIn {
		XVPath inPath1;    //输入路径
		XVPath inPath2;    //输入路径
		PathDistanceMode inPathDistanceMode;     //计算距离方式(df:PointtoPoint)
		int inResolution; //(range：0-999999)(df:1)
	};

	struct XVPathToPathDistanceProfileOut {
		vector<float> outDistance;         //输出距离
		vector<XVSegment2D> outConnectingSegments;   //输出距离的起点到终点的线段
		float outTime;
	};
	/*
	* @brief  计算路径到路径的剖面距离
	* return  [out] 0  函数执行成功
	*              -1  输入路径为空
	*/
	MAKEDLL_API int XVPathToPathDistanceProfile(XVPathToPathDistanceProfileIn& XVPathToPathDistanceProfile_In, XVPathToPathDistanceProfileOut& XVPathToPathDistanceProfileOut);

	struct XVPathPathIntersectionsIn {
		XVPath inPath1;//输入路径1
		XVPath inPath2;//输入路径2
	};
	struct XVPathPathIntersectionsOut {
		vector<XVPoint2D>outIntersectionPoints;//交点
		float outTime;//时间
	};

	/*
	* @brief  路径与路径交点
	* return  [out] 0  函数执行成功
	*              -1  输入路径为空
	*/
	MAKEDLL_API int  XVPathPathIntersections(XVPathPathIntersectionsIn& XVPathPathIntersections_In, XVPathPathIntersectionsOut& XVPathPathIntersections_Out);


	struct XVMatchShapesIn {
		XVPath inPath1;//输入路径1
		XVPath inPath2;//输入路径2
		int inMethod;     //匹配模式，取值1,2,3,共3种模式
	}; 

	struct XVMatchShapesOut {
		float outSimilarity;    //匹配相似度（越接近于0，形状匹配度越高）
		float outTime;//时间
	};

	/*
	* @brief  形状匹配
	* return  [out] 0  函数执行成功
	*              -1  输入路径为空
	*/
	MAKEDLL_API int XVMatchShapes(XVMatchShapesIn& XVMatchShapes_In, XVMatchShapesOut& XVMatchShapes_Out);


	struct XVPathSmoothPathMeanIn {
		XVPath inPath;    //输入路径
		int inKernelRadius; //核半径(range：0-999999)(df:1)
	};
	struct XVPathSmoothPathMeanOut {
		XVPath outPath;
		float outTime;//时间
	};
	/*
	* @brief   路径均值平滑
	* return  [out] 0  函数执行成功
	*/
	MAKEDLL_API int XVPathSmoothPathMean(XVPathSmoothPathMeanIn& XVPathSmoothPathMean_In, XVPathSmoothPathMeanOut& XVPathSmoothPathMean_Out);

	struct XVConvertToEquidistantPathIn {
		XVPath inPath;    //输入路径
		float inStep;     //特征点之间的间距(range：1.0f-999999.0f)(df:1.0f)
		bool inType = true;      //取值为true时，保留精度，取值为false时，保留长度，可能会损失精度
	};
	struct XVConvertToEquidistantPathOut {
		XVPath outPath;
		float outTime;//时间
	};

	/*
	* @brief   路径转换为等距路径
	* return  [out] 0  函数执行成功
	*              -1  输入路径为空
	*/
	MAKEDLL_API int XVConvertToEquidistantPath(XVConvertToEquidistantPathIn& XVConvertToEquidistantPath_In, XVConvertToEquidistantPathOut& XVConvertToEquidistantPath_Out);


	/***最小外接圆***/
	typedef struct XVPathBoundingEllipseIn
	{
		XVPath                           inPath;//(输入路径)
	}XVPathBoundingEllipseIn;

	typedef struct XVPathBoundingEllipseOut
	{
		XVEllipse2D                       outEllipse2D;//(最小外接椭圆)
		float                            outTime;
	}XVPathBoundingEllipseOut;
	/*
	* @brief   路径最小外接椭圆
	* return  [out] 0  函数执行成功
	*              -1  输入路径为空
	*/
	MAKEDLL_API int XVPathBoundingEllipse(XVPathBoundingEllipseIn& XVPathBoundingEllipse_In, XVPathBoundingEllipseOut& XVPathBoundingEllipse_Out);

	/***闭合路径并集***/
	typedef struct XVPathUnionIn
	{
		XVPath                    inPath1;//(输入路径)
		XVPath                    inPath2;//(输入路径)
	}XVPathUnionIn;

	typedef struct XVPathUnionOut
	{
		XVPath              outPath;//(合并后路径)
		float                        outTime;
	}XVPathUnionOut;
	/*
	* @brief   闭合路径并集
	* return  [out] 0  函数执行成功
	*              -1  输入路径为空
	*              -2  路径不闭合
	*              -3  输入路径存在自交
	*/
	MAKEDLL_API int XVPathUnion(XVPathUnionIn& XVPathUnion_In, XVPathUnionOut& XVPathUnion_Out);

	/***闭合路径交集***/
	typedef struct XVPathintersectionIn
	{
		XVPath                    inPath1;//(输入路径)
		XVPath                    inPath2;//(输入路径)
	}XVPathintersectionIn;

	typedef struct XVPathintersectionOut
	{
		vector<XVPath>               outPath;//(交集路径数组)
		float                        outTime;
	}XVPathintersectionOut;
	/*
	* @brief   闭合路径交集
	* return  [out] 0  函数执行成功
	*              -1  输入路径为空
	*              -2  路径不闭合
	*              -3  路径自交
	*/
	MAKEDLL_API int XVPathintersection(XVPathintersectionIn& XVPathintersection_In, XVPathintersectionOut& XVPathintersection_Out);

	/***闭合路径差集***/
	typedef struct XVPathDifferenceIn
	{
		XVPath                    inPath1;//(输入路径)
		XVPath                    inPath2;//(输入路径)
		bool                      inInverse = false;//默认为false，为false时，inPath1对inPath2作差集保留inPath1部分，为true时，inPath2对inPath1作差集保留inPath2部分
	}XVPathDifferenceIn;

	typedef struct XVPathDifferenceOut
	{
		vector<XVPath>               outPath;//(差集路径数组)
		vector<XVPath>               outHoles;//(孔洞数组，如两形状存在包含关系，做差集后，存在孔洞)
		float                        outTime;
	}XVPathDifferenceOut;
	/*
	* @brief   闭合路径差集
	* return  [out] 0  函数执行成功
	*              -1  输入路径为空
	*              -2  路径不闭合
	*              -3  路径自交 
	*/
	MAKEDLL_API int XVPathDifference(XVPathDifferenceIn& XVPathDifference_In, XVPathDifferenceOut& XVPathDifference_Out);


	/***闭合路径并集***/
	typedef struct XVPathAndHolesUnionIn
	{
		XVPath                    inPath1;//(输入路径)
		vector<XVPath>            inHoles1;
		XVPath                    inPath2;//(输入路径)
		vector<XVPath>            inHoles2;
	}XVPathAndHolesUnionIn;

	typedef struct XVPathAndHolesUnionOut
	{
		XVPath                       outPath;//(合并后路径)
		vector<XVPath>               outHoles;
		float                        outTime;
	}XVPathAndHolesUnionOut;
	/*
	* @brief   闭合路径并集
	* return  [out] 0  函数执行成功
	*              -1  输入路径为空
	*              -2  路径不闭合
	*              -3  输入路径存在自交
	*/
	MAKEDLL_API int XVPathAndHolesUnion(XVPathAndHolesUnionIn& XVPathAndHolesUnion_In, XVPathAndHolesUnionOut& XVPathAndHolesUnion_Out);

	/***闭合路径交集***/
	typedef struct XVPathAndHolesintersectionIn
	{
		XVPath                    inPath1;//(输入路径)
		vector<XVPath>            inHoles1;
		XVPath                    inPath2;//(输入路径)
		vector<XVPath>            inHoles2;
	}XVPathAndHolesintersectionIn;

	typedef struct XVPathAndHolesintersectionOut
	{
		vector<XVPath>               outPath;//(交集路径)
		vector<XVPath>               outHoles;
		float                        outTime;
	}XVPathAndHolesintersectionOut;
	/*
	* @brief   闭合路径交集
	* return  [out] 0  函数执行成功
	*              -1  输入路径为空
	*              -2  路径不闭合
	*              -3  路径自交
	*/
	MAKEDLL_API int XVPathAndHolesintersection(XVPathAndHolesintersectionIn& XVPathAndHolesintersection_In, XVPathAndHolesintersectionOut& XVPathAndHolesintersection_Out);

	/***闭合路径差集***/
	typedef struct XVPathAndHolesDifferenceIn
	{
		XVPath                    inPath1;//(输入路径)
		vector<XVPath>            inHoles1;
		XVPath                    inPath2;//(输入路径)
		vector<XVPath>            inHoles2;
		bool                      inInverse = false;//默认为false，为false时，inPath1对inPath2作差集保留inPath1部分，为true时，inPath2对inPath1作差集保留inPath2部分
	}XVPathAndHolesDifferenceIn;

	typedef struct XVPathAndHolesDifferenceOut
	{
		vector<XVPath>               outPath;//(差集路径数组)
		vector<XVPath>               outHoles;//(孔洞数组，如两形状存在包含关系，做差集后，存在孔洞)
		float                        outTime;
	}XVPathAndHolesDifferenceOut;
	/*
	* @brief   闭合路径差集
	* return  [out] 0  函数执行成功
	*              -1  输入路径为空
	*              -2  路径不闭合
	*              -3  路径自交
	*/
	MAKEDLL_API int XVPathAndHolesDifference(XVPathAndHolesDifferenceIn& XVPathAndHolesDifference_In, XVPathAndHolesDifferenceOut& XVPathAndHolesDifference_Out);
	//多边形修复

}  
#endif


