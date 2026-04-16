#ifndef MEASURE_H
#define MEASURE_H

/*************************************************
*
*             2D算法库测量函数头文件
*           Created on 2022.5.31 by wangxinping
*
*************************************************/

#include  "XVBase.h"

#define PI   3.1415926535897932384626433832795

namespace XVL
{
	//////////////////////
	//辅助函数数据结构
	typedef struct Phasor//表示向量
	{
		float x;
		float y;
	}Phasor;

	enum RotationDirection
	{
		Clockwise,//顺时针
		CounterClockwise,//逆时针
	};

	typedef struct RandGenerateIn//随机函数输入
	{
		float startValue;//生成范围起始值
		float endValue;//生成范围终止值
	}RandGenerateIn;

	typedef struct RandGenerateOut//随机函数输出
	{
		int result;//1为OK，2为NG
		float outValue;//随机值
	}RandGenerateOut;

	typedef struct LineToSegmentIn//直线转线段输入参数
	{
		XVLine2D inLine;//输入线段
		float x1;//线段起始点X坐标
		float x2;//线段终点Y坐标
	}LineToSegmentIn;

	typedef struct LineToSegmentOut//直线转线段输出参数
	{
		float time;//函数耗时
		int result;//1为OK，2为NG
		XVSegment2D outSegment;
	}LineToSegmentOut;

	typedef struct SegmentToLineIn//线段转直线输入参数
	{
		XVSegment2D inSegment;//输入线段
	}SegmentToLineIn;

	typedef struct SegmentToLineOut//线段转直线输出参数
	{
		float time;//函数耗时
		int result;//转化结果,1为OK，2为NG
		XVLine2D outLine;
	}SegmentToLineOut;

	//////////////////////
	//距离测量函数数据结构
	typedef struct XVPointToPointDistanceIn//点到点距离输入参数
	{
		//XVImage* inImage;//输入图像
		XVPoint2D inPoint1;//输入点1
		XVPoint2D inPoint2;//输入点2
		float inResolution;//像素当量,单位毫米
	}XVPointToPointDistanceIn;

	typedef struct XVPointToPointDistanceOut//点到点距离输出参数
	{
		float outTime;//函数耗时
		int outMeasureResult;//测量结果，1为OK，2为NG
		float outImageDistance;//图像距离
		float outPhyDistance;//物理距离，即实际测量值
		XVSegment2D outConnectingSegment;//连接线段
	}XVPointToPointDistanceOut;

	typedef struct XVPointToLineDistanceIn//点到直线距离输入0参数
	{
		//XVImage* inImage;//输入图像
		XVPoint2D inPoint;//输入点
		XVLine2D inLine;//输入直线
		float inResolution;//像素当量,单位毫米
	}XVPointToLineDistanceIn;

	typedef struct XVPointToLineDistanceOut//点到直线距离输出参数
	{
		float outTime;//函数耗时
		int outMeasureResult;//测量结果，1为OK，2为NG
		float outImageDistance;//图像距离
		float outPhyDistance;//物理距离，即实际测量值
		XVSegment2D outConnectingSegment;//连接线段，即点到直线之间的垂线段
	}XVPointToLineDistanceOut;

	typedef struct XVPointToSegmentDistanceIn//点到线段距离输入参数
	{
		//XVImage* inImage;//输入图像
		XVPoint2D inPoint;//输入点
		XVSegment2D inSegment;//输入线段
		float inResolution;//像素当量,单位毫米
	}XVPointToSegmentDistanceIn;

	typedef struct XVPointToSegmentDistanceOut//点到线段距离输出参数
	{
		float outTime;//函数耗时
		int outMeasureResult;//测量结果，1为OK，2为NG
		float outImageDistance;//图像距离
		float outPhyDistance;//物理距离，即实际测量值
		XVSegment2D outConnectingSegment;//连接线段，即点到线段距离之间的线段
	}XVPointToSegmentDistanceOut;

	typedef struct XVSegmentToSegmentDistanceIn//线段到线段距离输入参数
	{
		//XVImage* inImage;//输入图像
		XVSegment2D inSegment1;//输入线段1（起始线段）
		XVSegment2D inSegment2;//输入线段2（终止线段）
		float inResolution;//像素当量,单位毫米
	}XVSegmentToSegmentDistanceIn;

	typedef struct XVSegmentToSegmentDistanceOut//线段到线段距离输出参数
	{
		float outTime;//函数耗时
		int outMeasureResult;//测量结果，1为OK，2为NG
		
		float outMinImageDistance;//最短图像距离
		float outMinPhyDistance;//最短物理距离，即实际测量值
		XVSegment2D outMinConnectingSegment;//最短连接线段，即线段到线段距离之间的线段

		float outMaxImageDistance;//最大图像距离
		float outMaxPhyDistance;//最大物理距离，即实际测量值
		XVSegment2D outMaxConnectingSegment;//最大连接线段，即线段到线段距离之间的线段

		float outAverageImageDistance;//平均图像距离（最短与最大距离的均值）
		float outAveragePhyDistance;//平均物理距离，即实际测量值

	}XVSegmentToSegmentDistanceOut;

	typedef struct XVPointToCircleDistanceIn//点到圆距离输入参数
	{
		//XVImage* inImage;//输入图像
		XVPoint2D inPoint;//输入点
		XVCircle2D inCircle;//输入圆
		float inResolution;//像素当量,单位毫米
	}XVPointToCircleDistanceIn;

	typedef struct XVPointToCircleDistanceOut//点到圆距离输出参数
	{
		float outTime;//函数耗时
		int outMeasureResult;//测量结果，1为OK，2为NG
		float outImageDistance;//图像距离
		float outPhyDistance;//物理距离，即实际测量值
		XVSegment2D outConnectingSegment;//连接线段，即点到圆距离之间的线段
	}XVPointToCircleDistanceOut;

	typedef struct XVPointToArcDistanceIn//点到圆弧距离输入参数
	{
		//XVImage* inImage;//输入图像
		XVPoint2D inPoint;//输入点
		XVArc2D inArc;//输入圆弧
		float inResolution;//像素当量,单位毫米
	}XVPointToArcDistanceIn;

	typedef struct XVPointToArcDistanceOut//点到圆弧距离输出参数
	{
		float outTime;//函数耗时
		int outMeasureResult;//测量结果，1为OK，2为NG
		float outImageDistance;//图像距离
		float outPhyDistance;//物理距离，即实际测量值
		XVSegment2D outConnectingSegment;//连接线段，即点到圆弧距离之间的线段
	}XVPointToArcDistanceOut;

	typedef struct XVBoxToBoxDistanceIn//Box到Box距离测量输入函数
	{
		XVBox inBox1;//输入Box1
		XVBox inBox2;//输入Box2
		float inResolution;//像素当量，单位是毫米
	}XVBoxToBoxDistanceIn;

	typedef struct XVBoxToBoxDistanceOut//Box到Box距离测量输出函数
	{
		float outTime;//函数耗时
		int outMeasureResult;//测量结果，1为OK，2为NG
		float outImageDistance;//图像距离
		float outPhyDistance;//物理距离，即实际测量值
		XVSegment2D outConnectingSegment;//连接线段
	}XVBoxToBoxDistanceOut;

	typedef struct XVPointSequenceDistancesIn//点集间点距离测量输入参数
	{
		vector<XVPoint2D> inPoints;//输入点集
		bool inCyclic;//点集是否闭合，true为闭合，false为不闭合
		float inResolution;//像素当量,单位毫米
	}XVPointSequenceDistancesIn;

	typedef struct XVPointSequenceDistancesOut//点集间点距离测量输出参数
	{
		float outTime;//函数耗时
		int outMeasureResult;//测量结果，1为OK，2为NG
		vector<float> outImageDistances;//图像距离集
		vector<float> outPhyDistances;//物理距离集
		float outImageDistanceSum;//图像距离和
		float outPhyDistanceSum;//物理距离和
		vector<XVSegment2D> outConnectingSegments;//连接线段集
	}XVPointSequenceDistancesout;

	typedef struct XVCircleToCircleDistanceIn//圆到圆距离输入参数
	{
		//XVImage* inImage;//输入图像
		XVCircle2D inCircle1;//输入圆1
		XVCircle2D inCircle2;//输入圆2
		float inResolution;//像素当量,单位毫米
	}XVCircleToCircleDistanceIn;

	typedef struct XVCircleToCircleDistanceOut//圆到圆距离输出参数
	{
		float outTime;//函数耗时
		int outMeasureResult;//测量结果，1为OK，2为NG
		float outImageDistance;//图像距离
		float outPhyDistance;//物理距离，即实际测量值
		XVSegment2D outConnectingSegment;//连接线段，即圆到圆距离之间的线段
	}XVCircleToCircleDistanceOut;

	typedef struct XVLineToCircleDistanceIn//直线到圆距离输入参数
	{
		//XVImage* inImage;//输入图像
		XVLine2D inLine;//输入直线
		XVCircle2D inCircle;//输入圆
		float inResolution;//像素当量,单位毫米
	}XVLineToCircleDistanceIn;

	typedef struct XVLineToCircleDistanceOut//直线到圆距离输出参数
	{
		float outTime;//函数耗时
		int outMeasureResult;//测量结果，1为OK，2为NG
		float outImageDistance;//图像距离
		float outPhyDistance;//物理距离，即实际测量值
		XVSegment2D outConnectingSegment;//连接线段，即直线到圆距离之间的线段
	}XVLineToCircleDistanceOut;

	typedef struct XVPathToPointDistanceIn//点到路径间最短距离测量输入参数
	{
		XVPath inPath;//输入路径
		bool inCyclic;//路径是否闭合，true为闭合，false为不闭合
		XVPoint2D inPoint;//输入点
		float inResolution;//像素当量，单位毫米
	}XVPathToPointDistanceIn;

	typedef struct XVPathToPointDistanceOut//点到路径间最短距离测量输出参数
	{
		float outTime;//函数耗时
		int outMeasureResult;//测量结果，1为OK，2为NG
		float outImageDistance;//最短图像距离
		float outPhyDistance;//最短物理距离，即实际测量值
		XVSegment2D outConnectingSegment;//最短连接线段
	}XVPathToPointDistanceOut;

	typedef struct XVPathToLineDistanceIn//路径（多点）到直线距离输入参数
	{
		//XVImage* inImage;//输入图像
		XVPath inPoints;//输入路径（点集）
		XVLine2D inLine;//输入直线
		float inResolution;//像素当量,单位毫米
	}XVPathToLineDistanceIn;

	typedef struct XVPathToLineDistanceOut//路径（多点）到直线距离输出参数
	{
		float outTime;//函数耗时
		int outMeasureResult;//测量结果，1为OK，2为NG
		float outImageDistance;//图像距离
		float outPhyDistance;//物理距离，即实际测量值
		XVSegment2D outConnectingSegment;//连接线段，即路径（多点）到直线之间的最短垂线段
	}XVPathToLineDistanceOut;



	///////////////////////////
	//交点测量函数数据结构
	typedef struct XVLineToLineIntersectionIn//直线与直线交点输入参数
	{
		XVLine2D inLine1;
		XVLine2D inLine2;
	}XVLineToLineIntersectionIn;

	typedef struct XVLineToLineIntersectionOut//直线与直线交点输出参数
	{
		float outTime;//函数耗时
		int outMeasureResult;//测量结果，1为OK，2为NG
		XVPoint2D outPoint;//交点
	}XVLineToLineIntersectionOut;

	typedef struct XVLineToCircleIntersectionIn//直线与圆交点输入参数
	{
		XVLine2D inLine;//输入直线
		XVCircle2D inCircle;//输入圆
	}XVLineToCircleIntersectionIn;

	typedef struct XVLineToCircleIntersectionOut//直线与圆交点输出参数
	{
		float outTime;//函数耗时
		int outMeasureResult;//测量结果，1为OK，2为NG
		vector<XVPoint2D> outPoints;
	}XVLineToCircleIntersectionOut;
	typedef struct XVCircleToCircleIntersectionIn//圆与圆交点输入参数
	{
		XVCircle2D inCircle1;//输入圆1
		XVCircle2D inCircle2;//输入圆2
	}XVCircleToCircleIntersectionIn;

	typedef struct XVCircleToCircleIntersectionOut//圆与圆交点输出参数
	{
		float outTime;//函数耗时
		int outMeasureResult;//测量结果，1为OK，2为NG
		vector<XVPoint2D> outPoints;
	}XVCircleToCircleIntersectionOut;

	typedef struct XVLineToArc2DIntersectionIn//直线与圆弧交点输入参数
	{
		XVLine2D inLine2D;//输入直线
		XVArc2D inArc2D;//输入圆弧
	}XVLineToArc2DIntersectionIn;

	typedef struct XVLineToArc2DIntersectionOut//直线与圆弧交点输出参数
	{
		float outTime;//函数耗时
		vector<XVPoint2D> outPoints;  //交点
	}XVLineToArc2DIntersectionOut;

	//角度测量函数数据结构
	typedef struct XVAngleBetweenLinesIn//两直线角度测量输入参数
	{
		XVLine2D inLine1;//输入直线1
		XVLine2D inLine2;//输入直线2
	}XVAngleBetweenLinesIn;

	typedef struct XVAngleBetweenLinesOut//两直线角度测量输出参数
	{
		float outTime;//函数耗时
		int outMeasureResult;//测量结果，1为OK，2为NG
		float outSmallerAngle;//测量的偏小角度，单位为度
		float outLargerAngle;//测量的偏大角度，单位为度

	}XVAngleBetweenLinesOut;
	typedef struct XVAngleBetweenThreePointsIn//三点确定角度输入参数
	{
		XVPoint2D inPoint1;//输入点1
		XVPoint2D inPoint2;//输入点2
		XVPoint2D inPoint3;//输入点3
		RotationDirection inRotationDirection;//旋转方向
	}XVAngleBetweenThreePointsIn;

	typedef struct XVAngleBetweenThreePointsOut//三点确定角度输出参数
	{
		float outTime;//函数耗时
		int outMeasureResult;//测量结果，1为OK，2为NG
		float outAbsoluteAngle;//绝对角度
		float outDirectedAngle;//方向角度
		XVArc2D outArc;//输出圆弧
	}XVAngleBetweenThreePointsOut;

	//线段方向输入参数
	typedef struct XVSegmentOrientationIn
	{
		XVSegment2D inSegment;//输入线段(计算角度是以线段起点作为原点，向右为X轴正方向，角度范围0-360)
		RotationDirection inRotationDirection;//旋转方向，默认逆时针旋转方向为正方向
	}XVSegmentOrientationIn;

	//线段方向输出参数
	typedef struct XVSegmentOrientationOut
	{
		float outTime;//函数耗时
		int outMeasureResult;//测量结果，1为OK，2为NG
		float outOrientationAngle;
	}XVSegmentOrientationOut;

	//线段偏移方向
	enum OffsetOrientation
	{
		VerticalToSegUpwards,//垂直于线段向上
		VerticalToSegDownwards//垂直于线段向下
	};

	//线段平行输入参数
	typedef struct XVSegmentParallelIn
	{
		XVSegment2D inSegment;//输入线段
		float inOffsetDistance;//偏移距离，默认值为0，范围：offsetDistance > 0
		OffsetOrientation inOffsetOrientation;//偏移方向,默认值为VerticalToSegDownwards，即垂直于线段向下
	}XVSegmentParallelIn;

	//线段平行输出参数
	typedef struct XVSegmentParallelOut
	{
		float outTime;//函数耗时
		int outMeasureResult;//测量结果，1为OK，2为NG
		XVSegment2D outSegment;//输出线段
	}XVSegmentParallelOut;

	//线段旋转输入参数
	typedef struct XVRotateSegmentIn
	{
		XVSegment2D inSegment;//输入线段
		XVPoint2D inCenter;//旋转中心
		float inAngle;//输入角度
	}XVRotateSegmentIn;

	//线段旋转输出参数
	typedef struct XVRotateSegmentOut
	{
		float outTime;//函数耗时
		int outMeasureResult;//测量结果，1为OK，2为NG
		XVSegment2D outSegment;//输出线段
	}XVRotateSegmentOut;

	//点旋转输入参数
	typedef struct XVRotatePointIn
	{
		XVPoint2D inPoint;//输入点
		XVPoint2D inCenter;//旋转中心
		float inAngle;//输入角度
	}XVRotatePointIn;

	//点旋转输出参数
	typedef struct XVRotatePointOut
	{
		float outTime;//函数耗时
		int outMeasureResult;//测量结果，1为OK，2为NG
		XVPoint2D outPoint;//输出点
	}XVRotatePointOut;

	//点数组旋转输入参数
	typedef struct XVRotatePointArrayIn
	{
		std::vector<XVPoint2D> inPoints;//输入点
		XVPoint2D inCenter;//旋转中心
		float inAngle;//输入角度
	}XVRotatePointArrayIn;

	//点数组旋转输出参数
	typedef struct XVRotatePointArrayOut
	{
		float outTime;//函数耗时
		int outMeasureResult;//测量结果，1为OK，2为NG
		std::vector<XVPoint2D> outPoints;//输出点数组
	}XVRotatePointArrayOut;

	//圆旋转输入参数
	typedef struct XVRotateCircleIn
	{
		XVCircle2D inCircle;//输入圆
		XVPoint2D inCenter;//旋转中心
		float inAngle;//输入角度
	}XVRotateCircleIn;

	//圆旋转输出参数
	typedef struct XVRotateCircleOut
	{
		float outTime;//函数耗时
		int outMeasureResult;//测量结果，1为OK，2为NG
		XVCircle2D outCircle;//输出圆
	}XVRotateCircleOut;

	//圆弧旋转输入参数
	typedef struct XVRotateArcIn
	{
		XVArc2D inArc;//输入圆弧
		XVPoint2D inCenter;//旋转中心
		float inAngle;//输入角度
	}XVRotateArcIn;

	//圆弧旋转输出参数
	typedef struct XVRotateArcOut
	{
		float outTime;//函数耗时
		int outMeasureResult;//测量结果，1为OK，2为NG
		XVArc2D outArc;//输出圆弧
	}XVRotateArcOut;

	//矩形旋转输入参数
	typedef struct XVRotateRectangleIn
	{
		XVRectangle2D inRectangle;//输入矩形
		XVPoint2D inCenter;//旋转中心
		float inAngle;//输入角度
	}XVRotateRectangleIn;

	//矩形旋转输出参数
	typedef struct XVRotateRectangleOut
	{
		float outTime;//函数耗时
		int outMeasureResult;//测量结果，1为OK，2为NG
		XVRectangle2D outRectangle;//输出矩形
	}XVRotateRectangleOut;

	//线段均分输入参数
	typedef struct XVSplitSegmentIn
	{
		XVSegment2D inSegment;//输入线段
		int inCount;//均分数量，默认值为2，取值范围为大于等于1
	}XVSplitSegmentIn;

	//线段均分输出参数
	typedef struct XVSplitSegmentOut
	{
		float outTime;//函数耗时
		int outMeasureResult;//测量结果，1为OK，2为NG
		std::vector<XVPoint2D> outPoints;//输出均分点集
		std::vector<XVSegment2D> outSegments;//输出均分线段集
	}XVSplitSegmentOut;

	//两线段之间进行线性插值输入参数
	typedef struct XVLerpSegmentsIn
	{
		XVSegment2D inSegment1;//输入线段1
		XVSegment2D inSegment2;//输入线段2
		float inLambda;//输入线段之间的插值，默认值为0.5
	}XVLerpSegmentsIn;

	//两线段之间进行线性插值输出参数
	typedef struct XVLerpSegmentsOut
	{
		float outTime;//函数耗时
		int outMeasureResult;//测量结果，1为OK，2为NG
		XVSegment2D outSegment;//输出线段
	}XVLerpSegmentsOut;


	// 矩形坐标系转换
	typedef struct XVAlignRectangleIn
	{
		XVRectangle2D inRectangle;       //输入矩形
		XVCoordinateSystem2D inAlignment;    //局部坐标系
		bool inInverse = false;       //是否切换到逆变换
	}XVAlignRectangleIn;

	
	typedef struct XVAlignRectangleOut
	{
		XVRectangle2D outRectangle;    //变换后矩形
		float outTime;//函数耗时
	}XVAlignRectangleOut;

	//圆坐标系转换
	typedef struct XVAlignCircleIn    
	{
		XVCircle2D inCircle;        //输入圆
		XVCoordinateSystem2D inAlignment;   //局部坐标系
		bool inInverse = false;     //是否切换到逆变换
	}XVAlignCircleIn;

	
	typedef struct XVAlignCircleOut
	{
		XVCircle2D outCircle;      //变换后圆
		float outTime;//函数耗时
	}XVAlignCircleOut;

	//圆狐坐标系转换
	typedef struct XVAlignArcIn
	{
		XVArc2D inArc;           //输入圆弧
		XVCoordinateSystem2D inAlignment;        //局部坐标系
		bool inInverse = false;  //是否切换到逆变换
	}XVAlignArcIn;

	
	typedef struct XVAlignArcOut
	{ 
		XVArc2D outArc;          //输出圆弧
		float outTime;//函数耗时
	}XVAlignArcOut;

	typedef struct XVAlignSegmentIn
	{
		XVSegment2D inSegment;        //输入线段
		XVCoordinateSystem2D inAlignment;      //局部坐标系
		bool inInverse = false;      //是否切换到逆变换
	}XVAlignSegmentIn;


	typedef struct XVAlignSegmentOut
	{
		XVSegment2D outSegment;     //变换后线段
		float outTime;//函数耗时
	}XVAlignSegmentOut;

	typedef struct XVAlignPathIn
	{
		XVPath inPath;          //输入路径
		XVCoordinateSystem2D inAlignment;    //局部坐标系
		bool inInverse = false;    //是否切换到逆变换
	}XVAlignPathIn;


	typedef struct XVAlignPathOut
	{
		XVPath outPath;        //变换后路径
		float outTime;//函数耗时
	}XVAlignPathOut;

	typedef struct XVAlignLineIn
	{
		XVLine2D inLine;      //输入直线
		XVCoordinateSystem2D inAlignment;    //局部坐标系
		bool inInverse = false;   //是否切换到逆变换
	}XVAlignLineIn;


	typedef struct XVAlignLineOut
	{
		XVLine2D outLine;      //变换后直线
		float outTime;//函数耗时
	}XVAlignLineOut;

	typedef struct XVAlignPathArrayIn
	{
		vector<XVPath> inPaths;      //输入路径数组
		XVCoordinateSystem2D inAlignment;     //局部坐标系
		bool inInverse = false;     //是否切换到逆变换
	}XVAlignPathArrayIn;


	typedef struct XVAlignPathArrayOut
	{
		vector<XVPath> outPaths;     //变换后路径数组
		float outTime;//函数耗时
	}XVAlignPathArrayOut;


	//辅助函数
	//1 判断平面上点与线段的位置关系（矢量法）
	_declspec(dllexport) int pointToSegmentLocationJudge(XVPoint2D& inPoint_P, XVSegment2D& inSegment_AB);
	//2 生成随机数函数
	_declspec(dllexport) void RandGenerate(RandGenerateIn& randGenerateIn, RandGenerateOut& randGenerateOut);
	//3 直线转线段函数(主要用于显示)
	_declspec(dllexport) void LineToSegment(LineToSegmentIn& lineToSegmentIn, LineToSegmentOut& lineToSegmentOut);
	//4 线段转直线（新增）
	_declspec(dllexport) void SegmentToLine(SegmentToLineIn& segmentToLineIn, SegmentToLineOut& segmentToLineOut);

	//距离测量函数
	//1 点到点距离测量函数
	_declspec(dllexport) void XVPointToPointDistance(XVPointToPointDistanceIn& pointToPointDistanceIn, XVPointToPointDistanceOut& pointToPointDistanceOut);
	//2 点到直线距离测量函数
	_declspec(dllexport) void XVPointToLineDistance(XVPointToLineDistanceIn& pointToLineDistanceIn, XVPointToLineDistanceOut& pointToLineDistanceOut);
	//3 点到线段距离测量函数2：计算的距离是点到线段的最短距离（与AVS定义一致）
	_declspec(dllexport) void XVPointToSegmentDistance2(XVPointToSegmentDistanceIn& pointToSegmentDistanceIn, XVPointToSegmentDistanceOut& pointToSegmentDistanceOut);
	//4 线段到线段距离测量函数2：（定义与AVS一致）
	_declspec(dllexport) void XVSegmentToSegmentDistance2(XVSegmentToSegmentDistanceIn& segmentToSegmentDistanceIn, XVSegmentToSegmentDistanceOut& segmentToSegmentDistanceOut);
	//5 点到圆最短距离测量函数
	//点到圆最短距离按三种情况讨论：1）点在圆内；2）点在圆上；3)点在圆外
	_declspec(dllexport) void XVPointToCircleDistance(XVPointToCircleDistanceIn& pointToCircleDistanceIn, XVPointToCircleDistanceOut& pointToCircleDistanceOut);
	//6 点到圆弧距离测量函数
	_declspec(dllexport) void XVPointToArcDistance(XVPointToArcDistanceIn& pointToArcDistanceIn, XVPointToArcDistanceOut& pointToArcDistanceOut);
	//7 box到box的距离测量函数(即求两个矩形区域间的最短距离)
	_declspec(dllexport) void XVBoxToBoxDistance(XVBoxToBoxDistanceIn& boxToBoxDistanceIn, XVBoxToBoxDistanceOut& boxToBoxDistanceOut);
	//8 点集间点距离测量函数
	_declspec(dllexport) void XVPointSequenceDistances(XVPointSequenceDistancesIn& pointSequenceDistancesIn, XVPointSequenceDistancesOut& pointSequenceDistancesOut);
	//9 圆到圆距离测量函数
	//圆到圆最短距离按四种情况讨论：1）两圆内含；2）两圆相切（分外切和内切）；3）两圆相交；4）两圆相离
	_declspec(dllexport) void XVCircleToCircleDistance(XVCircleToCircleDistanceIn& circleToCircleDistanceIn, XVCircleToCircleDistanceOut& circleToCircleDistanceOut);
	//10 直线到圆距离测量函数:求的是过圆心的圆上的点到直线之间的最短垂线距离
	_declspec(dllexport) void XVLineToCircleDistance(XVLineToCircleDistanceIn& lineToCircleDistanceIn, XVLineToCircleDistanceOut& lineToCircleDistanceOut);
	//11 点到路径间最短距离测量函数(路径其实是连在一起的线段)
	_declspec(dllexport) void XVPathToPointDistance(XVPathToPointDistanceIn& pathToPointDistanceIn, XVPathToPointDistanceOut& pathToPointDistanceOut);//点到线段测量模式，与AVS定义一致
	//12 路径到直线最短距离测量函数：求路径到直线之间的最短距离
	_declspec(dllexport) void XVPathToLineDistance(XVPathToLineDistanceIn& pathToLineDistanceIn, XVPathToLineDistanceOut& pathToLineDistanceOut);

	//交点测量函数
	//1 直线与直线交点
	_declspec(dllexport) void XVLineToLineIntersection(XVLineToLineIntersectionIn& lineToLineIntersection, XVLineToLineIntersectionOut& lineToLineIntersectionOut);
	//2 直线与圆的交点测量函数
	_declspec(dllexport) void XVLineToCircleIntersection(XVLineToCircleIntersectionIn& lineToCircleIntersectionIn, XVLineToCircleIntersectionOut& lineToCircleIntersectionOut);
	//3 圆与圆的交点测量函数
	_declspec(dllexport) void XVCircleToCircleIntersection(XVCircleToCircleIntersectionIn& circleToCircleIntersectionIn, XVCircleToCircleIntersectionOut& circleToCircleIntersectionOut);
	//4 直线与圆弧的交点
	_declspec(dllexport) int XVLineToArc2DIntersection(XVLineToArc2DIntersectionIn& XVLineToArc2DIntersection_In, XVLineToArc2DIntersectionOut& XVLineToArc2DIntersection_Out);
	//return 0 执行成功




	//角度测量函数
	//1 求两直线夹角函数
	_declspec(dllexport) void XVAngleBetweenLines(XVAngleBetweenLinesIn& angleBetweenLinesIn, XVAngleBetweenLinesOut& angleBetweenLinesOut);
	//2 3点确定一个角度
	_declspec(dllexport) void XVAngleBetweenThreePoints(XVAngleBetweenThreePointsIn& angleBetweenThreePointsIn, XVAngleBetweenThreePointsOut& angleBetweenThreePointsOut);
	//3 求线段方向角度(计算的角度是以线段起点作为原点，向右为X轴正方向，线段终点与起点的连线与X轴的夹角，其角度范围0-360)
	_declspec(dllexport) void XVSegmentOrientation(XVSegmentOrientationIn& segmentOrientationIn, XVSegmentOrientationOut& segmentOrientationOut);

	//线段平行函数
	_declspec(dllexport) void XVSegmentParallel(XVSegmentParallelIn& segmentParallelIn, XVSegmentParallelOut& segmentParallelOut);

	//线段旋转函数
	_declspec(dllexport) void XVRotateSegment(XVRotateSegmentIn& rotateSegmentIn, XVRotateSegmentOut& rotateSegmentOut);
	//点旋转函数
	_declspec(dllexport) void XVRotatePoint(XVRotatePointIn& rotatePointIn, XVRotatePointOut& rotatePointOut);
	//点数组旋转函数
	_declspec(dllexport) void XVRotatePointArray(XVRotatePointArrayIn& rotatePointArrayIn, XVRotatePointArrayOut& rotatePointArrayOut);
	//圆旋转函数
	_declspec(dllexport) void XVRotateCircle(XVRotateCircleIn& rotateCircleIn, XVRotateCircleOut& rotateCircleOut);
	//圆弧旋转函数
	_declspec(dllexport) void XVRotateArc(XVRotateArcIn& rotateArcIn, XVRotateArcOut& rotateArcOut);
	//矩形旋转函数
	_declspec(dllexport) void XVRotateRectangle(XVRotateRectangleIn& rotateRectangleIn, XVRotateRectangleOut& rotateRectangleOut);

	//线段均分函数
	_declspec(dllexport) void XVSplitSegment(XVSplitSegmentIn& splitSegmentIn, XVSplitSegmentOut& splitSegmentOut);
	//两线段之间进行线性插值函数
	_declspec(dllexport) void XVLerpSegments(XVLerpSegmentsIn& lerpSegmentsIn, XVLerpSegmentsOut& lerpSegmentsOut);


	//坐标系转换
	//1、矩形转换
	_declspec(dllexport) int XVLAlignRectangle(XVAlignRectangleIn& XVAlignRectangle_In, XVAlignRectangleOut& XVAlignRectangle_Out);
	//2、圆形转换
	_declspec(dllexport) int XVLAlignCircle(XVAlignCircleIn& XVAlignCircle_In, XVAlignCircleOut& XVAlignCircle_Out);
	//3、圆弧转换
	_declspec(dllexport) int XVLAlignArc(XVAlignArcIn& XVAlignArc_In, XVAlignArcOut& XVAlignArc_Out);
	//4、线段转换
	_declspec(dllexport) int XVAlignSegment(XVAlignSegmentIn& XVAlignSegment_In, XVAlignSegmentOut& XVAlignSegment_Out);
	//5、路径转换
	_declspec(dllexport) int XVAlignPath(XVAlignPathIn& XVAlignPath_In, XVAlignPathOut& XVAlignPath_Out);
	//6、直线转换
	_declspec(dllexport) int XVAlignLine(XVAlignLineIn& XVAlignLine_In, XVAlignLineOut& XVAlignLine_Out);
	//7、路径数组转换
	_declspec(dllexport) int XVAlignPathArray(XVAlignPathArrayIn& XVAlignPathArray_In, XVAlignPathArrayOut& XVAlignPathArray_Out);

}


#endif
